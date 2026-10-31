"""Export the owned checkpoint to two CPU inference graphs; no database is shipped."""
import json
import pathlib
import sys
import inspect
import textwrap
import numpy as np
import torch
import onnxruntime as ort

ROOT=pathlib.Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT.parent/'PlacementModel20Slots'))
from model import PointerPlacer
from data import Encoder, MAX_EFFECTS
from train import tensors

ENCODER_KEYS=['piece_card','piece_tier','piece_ench','piece_cells','piece_run','skill_card','skill_tier','skill_run',
              'effect_card','effect_tier','effect_socket','effect_run','hero','numbers','piece_valid','skill_valid','effect_valid']
DECODER_KEYS=['memory','padding','piece_cells','low','width','piece_valid','socket_effect','order']

class Encode(torch.nn.Module):
    def __init__(self,model):super().__init__();self.model=model
    def forward(self,*args):
        b=dict(zip(ENCODER_KEYS,args));memory,padding,_=self.model.encode(b);return memory,padding

class Decode(torch.nn.Module):
    def __init__(self,model):super().__init__();self.model=model
    def forward(self,memory,padding,piece_cells,low,width,piece_valid,socket_effect,order):
        b=dict(piece_cells=piece_cells,low=low,width=width,piece_valid=piece_valid,socket_effect=socket_effect,
               effect_card=torch.zeros(memory.shape[0],MAX_EFFECTS,dtype=torch.int64,device=memory.device))
        return self.model.scores(memory,padding,20,b,order)

def main():
    torch.set_num_threads(1);torch.backends.mha.set_fastpath_enabled(False)
    # Equivalent mask construction using ONNX-supported operators (no in-place bool scatter).
    source=textwrap.dedent(inspect.getsource(PointerPlacer.scores))
    old='''picked = torch.zeros(batch, steps, count + 2, dtype=torch.bool, device=order.device)
    picked[:, 1:].scatter_(2, order[:, :-1, None], True)
    picked = picked.cummax(1).values[..., :count]'''
    new='''hot = torch.nn.functional.one_hot(order, count + 2)
    picked = torch.cat([torch.zeros_like(hot[:, :1]), hot[:, :-1]], 1)
    picked = (picked.cumsum(1) > 0)[..., :count]'''
    assert old in source;namespace={'torch':torch,'GAP':20,'END':21};exec(source.replace(old,new),namespace)
    PointerPlacer.scores=namespace['scores']
    saved=torch.load(ROOT/'model/model.pt',map_location='cpu',weights_only=False)
    state=saved['state'];a=saved['args']
    model=PointerPlacer(state['cards.idx'],state['cards.num'],state['cards.structure.weight'].shape[0]-1,
        heroes=state['hero.weight'].shape[0],enchantments=state['ench.weight'].shape[0],d=a['d'],layers=a['layers'],dropout=0)
    model.load_state_dict(state);model.eval()
    metadata=json.loads((ROOT/'model/metadata.full.json').read_text())
    vocab={g:i+1 for i,g in enumerate(metadata['vocabulary'])}
    sample=dict(hero='Jules',day=8,level=10,health=2500,low=0,high=9,pieces=[dict(id=g,tier='Bronze',ench='',cells=1) for g in list(vocab)[:20]],skills=[],effects=[])
    b=tensors(Encoder(vocab,saved['names'],frozen=True).batch([sample]),'cpu')
    encode,decode=Encode(model),Decode(model)
    torch.onnx.export(encode,tuple(b[k] for k in ENCODER_KEYS),str(ROOT/'model/encoder.onnx'),opset_version=17,
        dynamo=False,input_names=ENCODER_KEYS,output_names=['memory','padding'],
        dynamic_axes={k:{0:'batch'} for k in ENCODER_KEYS+['memory','padding']})
    with torch.no_grad():memory,padding=encode(*(b[k] for k in ENCODER_KEYS))
    order=torch.full((1,11),21,dtype=torch.long)
    args=(memory,padding,b['piece_cells'],b['low'],b['width'],b['piece_valid'],b['socket_effect'],order)
    torch.onnx.export(decode,args,str(ROOT/'model/decoder.onnx'),opset_version=17,dynamo=False,
        input_names=DECODER_KEYS,output_names=['logits'],dynamic_axes={k:{0:'batch'} for k in DECODER_KEYS+['logits']})
    sessions=[ort.InferenceSession(str(ROOT/'model'/f'{name}.onnx'),providers=['CPUExecutionProvider']) for name in ['encoder','decoder']]
    errors=[]
    for batch in [1,3,8]:
        bb={k:v.repeat_interleave(batch,0) for k,v in b.items()}
        with torch.no_grad():m,p=encode(*(bb[k] for k in ENCODER_KEYS))
        feed={k:bb[k].numpy() for k in ENCODER_KEYS};feed={x.name:feed[x.name] for x in sessions[0].get_inputs()}
        om,op=sessions[0].run(None,feed)
        err=float(np.max(np.abs(om-m.numpy())));errors.append(err);assert err<1e-4
        oo=order.repeat_interleave(batch,0);oo[:,0]=20;oo[:,1]=19
        with torch.no_grad():reference=decode(m,p,bb['piece_cells'],bb['low'],bb['width'],bb['piece_valid'],bb['socket_effect'],oo).numpy()
        feed=dict(memory=om,padding=op,piece_cells=bb['piece_cells'].numpy(),low=bb['low'].numpy(),width=bb['width'].numpy(),piece_valid=bb['piece_valid'].numpy(),socket_effect=bb['socket_effect'].numpy(),order=oo.numpy())
        actual=sessions[1].run(None,feed)[0];mask=reference>-1e8
        err=float(np.max(np.abs(actual[mask]-reference[mask])));errors.append(err);assert err<1e-4
        assert np.array_equal(actual<-1e8,reference<-1e8)
    # Only a small numeric baseline table is needed to encode run gains, not the game database.
    columns=['CooldownMax','DamageAmount','ShieldApplyAmount','HealAmount','BurnApplyAmount','PoisonApplyAmount','RegenApplyAmount','Multicast','AmmoMax','HasteAmount','ChargeAmount','CritChance','Lifesteal','FreezeAmount','SlowAmount','FlatCooldownReduction','PercentCooldownReduction','Custom_0','Custom_1','Custom_2','Custom_3']
    for card in metadata['templates'].values():
        card['tiers']={tier:{k:v for k,v in attrs.items() if k in columns} for tier,attrs in card['tiers'].items()}
    metadata['encoder_inputs']=[x.name for x in sessions[0].get_inputs()];metadata['decoder_inputs']=[x.name for x in sessions[1].get_inputs()]
    metadata['dimension']=a['d'];metadata['validation']='PyTorch/ONNX logits and masks compared at batches 1, 3, 8'
    (ROOT/'model/metadata.json').write_text(json.dumps(metadata,separators=(',',':')))
    report=dict(batch_sizes=[1,3,8],max_absolute_error=max(errors),mask_match=True)
    (ROOT/'model/onnx_validation.json').write_text(json.dumps(report,indent=2));print(report)

if __name__=='__main__':main()
