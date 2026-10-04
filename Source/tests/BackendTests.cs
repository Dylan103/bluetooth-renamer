using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BluetoothRenamer;

public static class BackendTests {
 private static int checks; private static string root;
 private static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;}
 private static void Throws(Action action,string name){bool thrown=false;try{action();}catch{thrown=true;}Check(thrown,name);}
 private static bool Equal(byte[] a,byte[] b){return a==null?b==null:b!=null&&a.SequenceEqual(b);}
 private static byte[] Enc(string name){return Encoding.UTF8.GetBytes(name+"\0");}
 public static int Main(string[] args){try{
  root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
  var normal=new Rig("normal",new byte[]{0});var result=normal.Rename("New name");
  Check(result.Device.Name=="New name"&&Equal(normal.Alias.Value,Enc("New name"))&&result.Entry.Status=="Verified","both requested values verified");
  Check(result.Entry.HasPersistentSnapshot&&Equal(result.Entry.OldAliasBytes,new byte[]{0})&&Equal(result.Entry.NewAliasBytes,Enc("New name")),"exact alias snapshots recorded");
  Check(normal.Store.GetEntries().Count==1&&normal.Store.GetEntries()[0].OldName=="Original"&&Equal(normal.Store.GetEntries()[0].OldAliasBytes,new byte[]{0}),"original name and bytes survive history serialization");
  Check(File.Exists(Path.Combine(result.Entry.BackupDirectory,"test-backup.txt")),"backup captured");
  Check(normal.DurableBeforeEveryWrite&&normal.Journal.IndexOf("backup")<normal.Journal.IndexOf("alias.write")&&normal.Journal.IndexOf("alias.write")<normal.Journal.IndexOf("api.write"),"durable history and backup precede alias then API writes");
  var undone=normal.Coordinator.Undo(result.Entry);
  Check(undone.Device.Name=="Original"&&Equal(normal.Alias.Value,new byte[]{0})&&normal.Store.GetEntries().Count==2,"undo restores API and exact single-null alias");
  Check(normal.Backups==2&&normal.DurableBeforeEveryWrite,"undo also backs up before writes");
  foreach(var sample in new[]{new AliasSample("absent",null),new AliasSample("empty",new byte[0]),new AliasSample("unicode",Enc("Ancien 🎧"))}){
   var rig=new Rig("undo-"+sample.Name,sample.Value);rig.Rename("Saved name");var loaded=rig.Store.GetEntries()[0];
   Check(loaded.HasPersistentSnapshot&&Equal(loaded.OldAliasBytes,sample.Value),sample.Name+" bytes round-trip through history");rig.Coordinator.Undo(loaded);
   Check(rig.Service.Name=="Original"&&Equal(rig.Alias.Value,sample.Value),sample.Name+" exact undo");
  }
  var same=new Rig("same-name",new byte[]{0});var persisted=same.Rename("Original");
  Check(persisted.Entry.Status=="Verified"&&same.Service.Calls==0&&same.Alias.Calls==1&&Equal(same.Alias.Value,Enc("Original")),"same API name gains persistent alias");
  int sameWrites=same.Alias.Calls;var noop=same.Rename("Original");
  Check(noop.Entry==null&&same.Alias.Calls==sameWrites&&same.Service.Calls==0&&same.Store.GetEntries().Count==1,"no-op only when API and persistent bytes match");
  same.Coordinator.Undo(persisted.Entry);Check(same.Service.Calls==0&&Equal(same.Alias.Value,new byte[]{0}),"same-name undo restores only previous alias");
  var stale=new Rig("stale",null);var staleEntry=stale.Rename("New").Entry;int apiCalls=stale.Service.Calls,aliasCalls=stale.Alias.Calls;
  stale.Service.Name="Changed elsewhere";Throws(delegate{stale.Coordinator.Undo(staleEntry);},"stale API undo rejected");
  Check(stale.Service.Calls==apiCalls&&stale.Alias.Calls==aliasCalls,"stale API undo makes no writes");
  stale.Service.Name="New";stale.Alias.Value=Enc("External alias");Throws(delegate{stale.Coordinator.Undo(staleEntry);},"stale alias undo rejected");
  Check(stale.Service.Calls==apiCalls&&stale.Alias.Calls==aliasCalls&&stale.Store.GetEntries().Count==1,"stale alias undo makes no writes or history");
  var selection=stale.Service.GetDevice(1);stale.Service.Name="New external name";Throws(delegate{stale.Coordinator.Rename(selection,"Wanted");},"stale selection rejected");
  Check(stale.Service.Calls==apiCalls&&stale.Alias.Calls==aliasCalls,"stale rename makes no writes");
  var legacy=new HistoryEntry{Address=1,OldName="Original",NewName=stale.Service.Name,HasPersistentSnapshot=false};
  Throws(delegate{stale.Coordinator.Undo(legacy);},"legacy entry cannot perform exact undo");
  Throws(delegate{NameRules.Validate(" ");},"blank name rejected");Throws(delegate{NameRules.Validate("a\n");},"control character rejected");
  Throws(delegate{NameRules.Validate(new string('x',248));},"oversized ASCII name rejected");Throws(delegate{NameRules.Validate("\uD800");},"unpaired surrogate rejected");
  Throws(delegate{NameRules.Validate(new string('\u00e9',124));},"UTF8 limit enforced below UTF16 character limit");
  NameRules.Validate(new string('x',247));Check(NameRules.EncodePersistentName(new string('x',247)).Length==248,"247-byte ASCII boundary plus terminator accepted");
  string emojiBoundary=String.Concat(Enumerable.Repeat("🎧",61))+"abc";Check(NameRules.EncodePersistentName(emojiBoundary).Length==248,"247-byte Unicode boundary accepted");
  Throws(delegate{NameRules.Validate(String.Concat(Enumerable.Repeat("🎧",62)));},"248-byte Unicode name rejected");
  Check(Equal(NameRules.EncodePersistentName("Café 🎧"),Enc("Café 🎧")),"UTF8 encoding has exactly one terminator");
  Throws(delegate{NameRules.ValidateAddress(0);},"zero address rejected");Throws(delegate{NameRules.ValidateAddress(0x1000000000000UL);},"address beyond six bytes rejected");
  var blocked=new Rig("backup-failure",null);blocked.FailBackup=true;Throws(delegate{blocked.Rename("New");},"backup failure surfaced");
  Check(blocked.Service.Calls==0&&blocked.Alias.Calls==0&&blocked.Status=="Failed","backup failure prevents both mutations");
  var aliasFail=new Rig("alias-write-failure",new byte[]{0});aliasFail.Alias.Mode=1;Throws(delegate{aliasFail.Rename("New");},"alias write failure surfaced");
  Check(aliasFail.Service.Calls==0&&aliasFail.Status=="Failed"&&Equal(aliasFail.Alias.Value,new byte[]{0}),"unchanged alias failure prevents API write and records Failed");
  var partial=new Rig("api-failure-after-alias",null);partial.Service.Mode=1;Throws(delegate{partial.Rename("New");},"partial API failure surfaced");
  Check(partial.Status=="Uncertain"&&partial.Service.Name=="Original"&&Equal(partial.Alias.Value,Enc("New")),"partial write cannot be marked Verified or unchanged Failed");
  var late=new Rig("verified-after-api-error",null);late.Service.Mode=2;var lateResult=late.Rename("New");
  Check(lateResult.Entry.Status=="Verified"&&Equal(late.Alias.Value,Enc("New"))&&!String.IsNullOrEmpty(lateResult.Warning),"dual rereads resolve API write-then-error");
  var aliasLate=new Rig("verified-after-alias-error",null);aliasLate.Alias.Mode=2;var aliasLateResult=aliasLate.Rename("Original");
  Check(aliasLateResult.Entry.Status=="Verified"&&aliasLate.Service.Calls==0&&!String.IsNullOrEmpty(aliasLateResult.Warning),"same-name alias write-then-error resolved by dual rereads");
  var unknown=new Rig("unreadable-api",null);unknown.Service.Mode=3;Throws(delegate{unknown.Rename("New");},"unreadable API outcome surfaced");Check(unknown.Status=="Uncertain","unreadable API is never verified");
  var unreadableAlias=new Rig("unreadable-alias",null);unreadableAlias.Alias.Mode=3;Throws(delegate{unreadableAlias.Rename("New");},"unreadable alias outcome surfaced");Check(unreadableAlias.Status=="Uncertain","unreadable alias is never verified");
  var lyingApi=new Rig("independent-api-read",null);lyingApi.Service.Mode=4;Throws(delegate{lyingApi.Rename("New");},"API return alone cannot verify rename");Check(lyingApi.Status=="Uncertain","independent API reread detects partial state");
  var ignoredAlias=new Rig("ignored-alias-write",null);ignoredAlias.Alias.Mode=4;Throws(delegate{ignoredAlias.Rename("New");},"silent alias write failure detected");Check(ignoredAlias.Status!="Verified","independent persistent reread required");
  var interrupted=new HistoryStore(Path.Combine(root,"interrupted"),delegate{});interrupted.Prepare(new Fake().GetDevice(1),"New");Check(interrupted.GetEntries()[0].Status=="Uncertain","interrupted operation treated as uncertain");
  Check(Marshal.SizeOf(typeof(BluetoothService.NativeDevice))==560,"native device ABI size");Check(Marshal.OffsetOf(typeof(BluetoothService.NativeDevice),"Name").ToInt32()==64,"native name ABI offset");Check(Marshal.SizeOf(typeof(BluetoothService.NativeSearch))==40,"native search ABI size");
  Console.WriteLine("PASS: "+checks+" backend checks. Real Bluetooth devices and registry values were not changed.");return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
 private sealed class AliasSample{public string Name;public byte[] Value;public AliasSample(string name,byte[] value){Name=name;Value=value;}}
 private sealed class Rig{
  public Fake Service=new Fake();public FakeAlias Alias;public HistoryStore Store;public RenameCoordinator Coordinator;public List<string> Journal=new List<string>();public bool FailBackup;public int Backups;public bool DurableBeforeEveryWrite=true;
  public string Status{get{return Store.GetEntries()[0].Status;}}
  public RenameResult Rename(string name){return Coordinator.Rename(Service.GetDevice(1),name);}
  public Rig(string name,byte[] original){
   Alias=new FakeAlias{Value=original};
   Store=new HistoryStore(Path.Combine(root,name),delegate(ulong address,string directory){Journal.Add("backup");if(FailBackup)throw new IOException("backup denied");File.WriteAllText(Path.Combine(directory,"test-backup.txt"),"fake");Backups++;});
   Service.BeforeWrite=delegate{CheckDurable("api.write");};Alias.BeforeWrite=delegate{CheckDurable("alias.write");};Coordinator=new RenameCoordinator(Service,Store,Alias);
  }
  private void CheckDurable(string operation){
   Journal.Add(operation);var entry=Store.GetEntries().FirstOrDefault();
   bool valid=entry!=null&&entry.HasPersistentSnapshot&&File.Exists(Path.Combine(entry.BackupDirectory,"test-backup.txt"))&&File.ReadAllText(Path.Combine(entry.BackupDirectory,"entry.json")).Contains("\"Status\":\"Updating\"");
   DurableBeforeEveryWrite=DurableBeforeEveryWrite&&valid;
  }
 }
 private sealed class Fake:IBluetoothService{
  public string Name="Original";public int Calls;public int Mode;public Action BeforeWrite;
  public List<DeviceInfo> ListDevices(){return new List<DeviceInfo>{GetDevice(1)};}
  public DeviceInfo GetDevice(ulong address){if(Mode==3&&Calls>0)throw new IOException("device unavailable");return new DeviceInfo{Address=1,AddressText="00:00:00:00:00:01",Name=Name,Kind="Audio"};}
  public DeviceInfo Rename(ulong address,string expected,string desired){if(BeforeWrite!=null)BeforeWrite();Calls++;if(Mode==1)throw new IOException("write failed");if(Mode==4)return new DeviceInfo{Address=1,Name=desired};Name=desired;if(Mode==2||Mode==3)throw new IOException("verification interrupted");return GetDevice(address);}
 }
 private sealed class FakeAlias:IPersistentNameStore{
  public byte[] Value;public int Calls;public int Mode;public Action BeforeWrite;
  public byte[] Read(ulong address){if(Mode==3&&Calls>0)throw new IOException("persistent bytes unavailable");return Value==null?null:(byte[])Value.Clone();}
  public void Write(ulong address,byte[] value){if(BeforeWrite!=null)BeforeWrite();Calls++;if(Mode==1)throw new IOException("persistent write failed");if(Mode==4)return;Value=value==null?null:(byte[])value.Clone();if(Mode==2||Mode==3)throw new IOException("persistent verification interrupted");}
 }
}
