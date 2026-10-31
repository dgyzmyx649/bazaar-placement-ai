"""Package the current native-score model and audited runtime, without vendor core/game data."""
import pathlib,json,hashlib,shutil,zipfile
source=pathlib.Path(__file__).resolve().parent;root=source.parents[1]
release=root/'outputs/PlacementModelV4/BazaarPlacementAI-0.1.4-preview';plugin=release/'BepInEx/plugins/BazaarPlacementAI'
assert json.loads((source/'model/metadata.json').read_text())['schema']=='placement-native-score-ranker-v4'
(plugin/'model').mkdir(parents=True,exist_ok=True)
shutil.copy2(source/'bin/Release/net472/BazaarPlacementAI.dll',plugin/'BazaarPlacementAI.dll')
for directory,names in [('LookingIN',['LookingIN.Compute.Client.dll','LookingIN.Compute.Contracts.dll']),('runtime',['Microsoft.ML.OnnxRuntime.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll'])]:
 for name in names:shutil.copy2(source/'dependencies'/directory/name,plugin/name)
for name in ['ranker.json','metadata.json','model-info.json']:shutil.copy2(source/'model'/name,plugin/'model'/name)
for name in ['README.txt','LICENSE.txt','MODEL-TERMS.txt']:shutil.copy2(source/name,plugin/name)
shutil.copy2(source/'README.txt',release/'README.txt')
public=release/'source';public.mkdir(exist_ok=True)
for name in ['Core.cs','Compute.cs','GameAdapter.cs','ModelRunner.cs','RankFeatures.cs','TreeRanker.cs','CandidateMoves.cs','SearchPolicy.cs','NativeRouter.cs','Plugin.cs','UIHints.cs','BazaarPlacementAI.csproj','build.ps1','LICENSE.txt']:shutil.copy2(source/name,public/name)
shutil.copytree(source/'dependencies/LookingIN',public/'dependencies/LookingIN',dirs_exist_ok=True)
report=json.loads((root/'outputs/PlacementModelV4/reports/acceptance-report.json').read_text());plugin_hash=hashlib.sha256((plugin/'BazaarPlacementAI.dll').read_bytes()).hexdigest()
assert report['plugin_sha256']==plugin_hash,'Code changed since acceptance: rerun checks and update report before packaging'
assert report['model_sha256']==hashlib.sha256((plugin/'model/ranker.json').read_bytes()).hexdigest()
assert report['mono_integration_checks']>=32 and report['strict_below_current']==0
for p in release.rglob('*'):
 if p.is_file():assert p.name not in ['LookingIN.dll','LookingIN.Compute.Worker.exe','GameData.db','model.pt','metadata.full.json']
hashes={p.relative_to(release).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(release.rglob('*')) if p.is_file() and p.name!='SHA256.json'}
(release/'SHA256.json').write_text(json.dumps(hashes,indent=2))
archive=release.parent/(release.name+'.zip')
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for p in sorted(release.rglob('*')):
  if p.is_file():z.write(p,p.relative_to(release))
print(json.dumps(dict(archive=str(archive),files=len(hashes),bytes=archive.stat().st_size,plugin_sha256=plugin_hash)))
