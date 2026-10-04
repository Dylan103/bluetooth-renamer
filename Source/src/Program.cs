using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Bluetooth Renamer")]
[assembly: AssemblyDescription("Rename paired Bluetooth devices on this Windows PC.")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

namespace BluetoothRenamer {
 public static class Program {
  [STAThread] public static int Main(string[] args) {
   try {
    bool demo=args.Contains("--demo");
    string dataRoot=Option(args,"--data-dir");
    if(!demo && dataRoot!=null) throw new ArgumentException("A custom data folder is only available in preview mode.");
    var application=new Application();
    application.ShutdownMode=ShutdownMode.OnMainWindowClose;
    application.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
     MessageBox.Show("Something interrupted the app. No additional changes will be made.\n\n"+e.Exception.Message,"Bluetooth Renamer",MessageBoxButton.OK,MessageBoxImage.Error);
     e.Handled=true;
    };
    var store=demo ? new HistoryStore(dataRoot ?? Path.Combine(Path.GetTempPath(),"BluetoothRenamerPreview"),delegate(ulong address,string dir) { File.WriteAllText(Path.Combine(dir,"preview.txt"),"Example device; no registry keys read or changed."); }) : new HistoryStore();
    IBluetoothService service=demo ? (IBluetoothService)new PreviewBluetoothService() : new BluetoothService();
    var controller=new MainController(service,store,demo,Option(args,"--diagnostics"),Option(args,"--select"),Option(args,"--draft"),Option(args,"--history"));
    application.Run(controller.Window);
    return 0;
   } catch(Exception ex) {
    MessageBox.Show("Bluetooth Renamer could not start.\n\n"+ex.Message,"Bluetooth Renamer",MessageBoxButton.OK,MessageBoxImage.Error);
    return 1;
   }
  }
  private static string Option(string[] args,string name) { int i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : null; }
 }

 public sealed class DeviceRow {
  public DeviceInfo Device {get;private set;}
  public string Name {get{return Device.Name;}}
  public string Subtitle {get{return (Device.Connected?"Connected":"Not connected")+"  ·  "+Device.AddressText;}}
  public DeviceRow(DeviceInfo device){Device=device;}
  public override string ToString(){return Name+", "+Subtitle;}
 }
 public sealed class HistoryRow {
  public HistoryEntry Entry {get;private set;}
  public string Title {get{return Entry.OldName+"  →  "+Entry.NewName;}}
  public string Subtitle {get{return Entry.TimeUtc.ToLocalTime().ToString("g",CultureInfo.CurrentCulture)+"  ·  "+Entry.AddressText+"  ·  "+Entry.Status;}}
  public HistoryRow(HistoryEntry entry){Entry=entry;}
  public override string ToString(){return Title+", "+Subtitle;}
 }

 public sealed class MainController {
  public Window Window {get;private set;}
  private IBluetoothService service;
  private HistoryStore store;
  private RenameCoordinator coordinator;
  private List<DeviceInfo> devices=new List<DeviceInfo>();
  private ListBox deviceList,historyList;
  private TextBox search,newName;
  private TextBlock selectedName,selectedInfo,nameHint,nameCount,status,empty,historyEmpty,count;
  private Button refresh,rename,undo,openHistory;
  private TabControl tabs;
  private Grid deviceLayout;
  private Border deviceCard,renameCard;
  private bool busy,filtering;
  private string diagnostics;
  private string historyNotice;
  private bool canEdit;
  [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);

  public MainController(IBluetoothService service,HistoryStore store,bool demo,string diagnostics,string initialAddress=null,string initialDraft=null,string initialHistory=null){
   this.service=service;this.store=store;this.coordinator=new RenameCoordinator(service,store,demo?(IPersistentNameStore)new PreviewPersistentNameStore():new RegistryPersistentNameStore());this.diagnostics=diagnostics;
   using(var identity=WindowsIdentity.GetCurrent()){canEdit=demo || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")){ Window=(Window)XamlReader.Load(stream); }
   if(demo){Window.Title="Bluetooth Renamer — Preview";Get<Border>("DemoBanner").Visibility=Visibility.Visible;}
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico")){if(stream!=null)Window.Icon=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);}
   if(SystemParameters.HighContrast)ApplyHighContrast();
   deviceList=Get<ListBox>("DeviceList");historyList=Get<ListBox>("HistoryList");search=Get<TextBox>("SearchBox");newName=Get<TextBox>("NewNameBox");
   selectedName=Get<TextBlock>("SelectedName");selectedInfo=Get<TextBlock>("SelectedInfo");nameHint=Get<TextBlock>("NameHint");nameCount=Get<TextBlock>("NameCount");
   status=Get<TextBlock>("StatusLabel");empty=Get<TextBlock>("EmptyLabel");historyEmpty=Get<TextBlock>("HistoryEmpty");count=Get<TextBlock>("DeviceCount");
   refresh=Get<Button>("RefreshButton");rename=Get<Button>("RenameButton");undo=Get<Button>("UndoButton");openHistory=Get<Button>("OpenHistoryButton");tabs=Get<TabControl>("Tabs");
   rename.Content=canEdit?"_Save name":"_Enable saving…";
   undo.Content=canEdit?"Restore _previous name":"Enable _restore…";
   Get<TextBlock>("PermissionHint").Text=canEdit?"Saves the name for future restarts too.":"Enable saving to approve administrator access, then save your name.";
   deviceLayout=Get<Grid>("DeviceLayout");deviceCard=Get<Border>("DeviceCard");renameCard=Get<Border>("RenameCard");
   refresh.Click+=async delegate { await RefreshAsync(null); };
   rename.Click+=async delegate {await RenameAsync();};
   undo.Click+=async delegate {await UndoAsync();};
   Get<Button>("HelpButton").Click+=delegate {ShowHelp();};
   openHistory.Click+=delegate {try{Directory.CreateDirectory(store.RootDirectory);Process.Start(new ProcessStartInfo(store.RootDirectory){UseShellExecute=true});}catch(Exception ex){SetStatus(ex.Message,true);}};
   search.TextChanged+=delegate {if(!busy)ApplyFilter(null);};
   newName.TextChanged+=delegate {UpdateButtons();};
   deviceList.SelectionChanged+=delegate {if(!filtering)SelectDevice();};
   historyList.SelectionChanged+=delegate {UpdateButtons();};
   Window.PreviewKeyDown+=OnKeyDown;
   Window.SizeChanged+=delegate {ResponsiveLayout();};
   Window.Closing+=delegate(object sender,CancelEventArgs e){if(busy){e.Cancel=true;SetStatus("Finishing the current operation. Please wait before closing.",false);}};
   Window.SourceInitialized+=delegate {
    DpiSupport.Attach(Window);
    if(!SystemParameters.HighContrast){try{int dark=1;DwmSetWindowAttribute(new WindowInteropHelper(Window).Handle,20,ref dark,4);}catch{}}
   };
   Window.Loaded+=async delegate {
    ResponsiveLayout();ulong address;ulong? preserve=UInt64.TryParse(initialAddress,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out address)?(ulong?)address:null;
    await RefreshAsync(preserve);
    if(preserve.HasValue && Selected!=null && Selected.Address==preserve.Value && initialDraft!=null)newName.Text=Encoding.UTF8.GetString(Convert.FromBase64String(initialDraft));
    if(initialHistory!=null){string id=Encoding.UTF8.GetString(Convert.FromBase64String(initialHistory));tabs.SelectedIndex=1;historyList.SelectedItem=historyList.Items.Cast<HistoryRow>().FirstOrDefault(r=>r.Entry.Id==id);}
    WriteDiagnostics();
   };
  }
  private T Get<T>(string name) where T:FrameworkElement {return (T)Window.FindName(name);}
  private void ApplyHighContrast(){
   Window.Resources["BackgroundBrush"]=SystemColors.WindowBrush;Window.Resources["CardBrush"]=SystemColors.WindowBrush;Window.Resources["InputBrush"]=SystemColors.WindowBrush;
   Window.Resources["TextBrush"]=SystemColors.WindowTextBrush;Window.Resources["MutedBrush"]=SystemColors.WindowTextBrush;Window.Resources["LineBrush"]=SystemColors.WindowTextBrush;
   Window.Resources["AccentBrush"]=SystemColors.HighlightBrush;Window.Resources["AccentTextBrush"]=SystemColors.HighlightTextBrush;Window.Resources["SelectedBrush"]=SystemColors.ControlBrush;Window.Resources["WarningBrush"]=SystemColors.WindowTextBrush;
  }
  private DeviceInfo Selected {get {var row=deviceList.SelectedItem as DeviceRow;return row==null?null:row.Device;}}
  private void SetBusy(bool value){busy=value;refresh.IsEnabled=!value;search.IsEnabled=!value;deviceList.IsEnabled=!value;historyList.IsEnabled=!value;openHistory.IsEnabled=!value;UpdateButtons();}
  private void SetStatus(string text,bool error){status.Text=text;status.Foreground=(Brush)Window.FindResource(error?"WarningBrush":"MutedBrush");try{var peer=UIElementAutomationPeer.FromElement(status);if(peer!=null)peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);}catch{}}
  private static string FriendlyError(Exception ex){
   var native=ex as Win32Exception;
   if(native!=null && native.NativeErrorCode==5)return "Windows denied access. No additional change was attempted. If this PC requires it, close the app and choose Run as administrator.\n"+ex.Message;
   return ex.Message;
  }
  private async Task RefreshAsync(ulong? preserve){
   if(busy)return;
   if(!preserve.HasValue && Selected!=null)preserve=Selected.Address;
   SetBusy(true);SetStatus("Reading paired Bluetooth devices…",false);
   try{
    devices=await Task.Run(delegate {return service.ListDevices();});
    ApplyFilter(preserve);
    await LoadHistoryAsync();
    string ready=devices.Count==0?"No paired classic Bluetooth devices found. Check Bluetooth in Windows Settings, then refresh.":"Ready. Select a device to edit its name. Changes apply on this PC.";
    SetStatus(ready+(String.IsNullOrEmpty(historyNotice)?"":" "+historyNotice),!String.IsNullOrEmpty(historyNotice));
   }catch(Exception ex){if(devices.Count==0){empty.Text="The device list is unavailable. Check the message below, then refresh.";empty.Visibility=Visibility.Visible;}SetStatus(FriendlyError(ex),true);}
   finally{SetBusy(false);}
  }
  private async Task LoadHistoryAsync(){
   try{var entries=await Task.Run(delegate{return store.GetEntries();});historyNotice=store.LastLoadWarning;historyList.ItemsSource=entries.Select(e=>new HistoryRow(e)).ToList();historyEmpty.Text=String.IsNullOrEmpty(historyNotice)?"Your saved name changes will appear here.":historyNotice;historyEmpty.Visibility=entries.Count==0?Visibility.Visible:Visibility.Collapsed;}
   catch(Exception ex){historyNotice="History could not be loaded: "+ex.Message;historyList.ItemsSource=null;historyEmpty.Text=historyNotice;historyEmpty.Visibility=Visibility.Visible;}
  }
  private void ApplyFilter(ulong? preserve){
   if(!preserve.HasValue && Selected!=null)preserve=Selected.Address;
   string q=search.Text.Trim();
   var rows=devices.Where(d=>q.Length==0 || d.Name.IndexOf(q,StringComparison.CurrentCultureIgnoreCase)>=0 || d.AddressText.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0).OrderByDescending(d=>d.Connected).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).Select(d=>new DeviceRow(d)).ToList();
   filtering=true;deviceList.ItemsSource=rows;
   DeviceRow chosen=preserve.HasValue?rows.FirstOrDefault(d=>d.Device.Address==preserve.Value):null;
   deviceList.SelectedItem=chosen ?? rows.FirstOrDefault();filtering=false;
   count.Text=rows.Count.ToString(CultureInfo.CurrentCulture);
   empty.Visibility=rows.Count==0?Visibility.Visible:Visibility.Collapsed;
   empty.Text=devices.Count==0?"No paired devices found. Turn on Bluetooth, pair a device in Windows Settings, then refresh.":"No devices match this filter.";
   SelectDevice();
  }
  private void SelectDevice(){
   var d=Selected;
   selectedName.Text=d==null?"Choose a device":d.Name;
   selectedInfo.Text=d==null?"Select a paired device from the list to change its name.":(d.Connected?"Connected":"Not connected")+" · "+d.Kind+"\n"+d.AddressText;
   newName.Text=d==null?"":d.Name;UpdateButtons();
  }
  private void UpdateButtons(){
   var d=Selected;string name=newName.Text;
   bool valid=true;string validation=null;try{NameRules.Validate(name);}catch(ArgumentException ex){valid=false;validation=ex.Message;}
   nameCount.Text=Encoding.UTF8.GetByteCount(name).ToString(CultureInfo.CurrentCulture)+" / 247 bytes";
   nameHint.Text=!valid && name.Length>0?validation:"Accented letters and emoji use more than one byte.";
   newName.IsEnabled=!busy && d!=null;
   rename.IsEnabled=!busy && d!=null && valid;
   var row=historyList.SelectedItem as HistoryRow;
   undo.IsEnabled=!busy && row!=null && row.Entry.HasPersistentSnapshot && String.Equals(row.Entry.Status,"Verified",StringComparison.OrdinalIgnoreCase);
   undo.ToolTip=row!=null && !row.Entry.HasPersistentSnapshot?"This older entry has no saved persistent name. Enter the previous name on the Devices tab to restore it.":"Restore the previous name and its saved restart setting.";
  }
  private async Task RenameAsync(){
   if(busy||!rename.IsEnabled)return;
   DeviceInfo selected=Selected;string desired=newName.Text;
   if(!canEdit){RelaunchElevated(selected,desired,null);return;}
   SetBusy(true);SetStatus("Saving the previous name and updating this device…",false);
   try{
    RenameResult result=await Task.Run(delegate{return coordinator.Rename(selected,desired);});
    RevealUpdatedDevice(result.Device);
    SetStatus(String.IsNullOrEmpty(result.Warning)?"Saved “"+result.Device.Name+"” for this session and future restarts. Reconnect if Quick Settings still shows the old name.":result.Warning,false);
   }catch(Exception ex){SetStatus(FriendlyError(ex),true);}
   finally{SetBusy(false);}
   await LoadHistoryAsync();
  }
  private async Task UndoAsync(){
   var row=historyList.SelectedItem as HistoryRow;if(busy||row==null||!undo.IsEnabled)return;
   if(!canEdit){RelaunchElevated(Selected,newName.Text,row.Entry.Id);return;}
   if(MessageBox.Show(Window,"Restore this device's name to “"+row.Entry.OldName+"”?\n\n"+row.Entry.AddressText,"Restore previous name",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)!=MessageBoxResult.OK)return;
   SetBusy(true);SetStatus("Restoring the previous name…",false);
   try{var result=await Task.Run(delegate{return coordinator.Undo(row.Entry);});RevealUpdatedDevice(result.Device);SetStatus("Restored “"+result.Device.Name+"”. Reconnect the device if its displayed name has not refreshed.",false);}
   catch(Exception ex){SetStatus(FriendlyError(ex),true);}
   finally{SetBusy(false);}
   await LoadHistoryAsync();
  }
  private void RelaunchElevated(DeviceInfo selected,string draft,string historyId){
   try{
    string args=selected==null?"":"--select "+selected.Address.ToString("X12",CultureInfo.InvariantCulture);
    if(selected!=null && !String.IsNullOrEmpty(draft))args+=" --draft "+Convert.ToBase64String(Encoding.UTF8.GetBytes(draft));
    if(historyId!=null)args+=" --history "+Convert.ToBase64String(Encoding.UTF8.GetBytes(historyId));
    var elevated=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,args){UseShellExecute=true,Verb="runas"});
    if(elevated==null)throw new InvalidOperationException("Windows did not open the editing window. Try again.");
    elevated.Dispose();Window.Close();
   }catch(Win32Exception ex){SetStatus(ex.NativeErrorCode==1223?"Administrator approval was cancelled. Nothing was changed.":ex.Message,true);}
   catch(Exception ex){SetStatus(ex.Message,true);}
  }
  private void ReplaceDevice(DeviceInfo updated){int i=devices.FindIndex(d=>d.Address==updated.Address);if(i>=0)devices[i]=updated;else devices.Add(updated);}
  private void RevealUpdatedDevice(DeviceInfo updated){ReplaceDevice(updated);string q=search.Text.Trim();if(q.Length>0 && updated.Name.IndexOf(q,StringComparison.CurrentCultureIgnoreCase)<0 && updated.AddressText.IndexOf(q,StringComparison.OrdinalIgnoreCase)<0)search.Text="";ApplyFilter(updated.Address);}
  private async void OnKeyDown(object sender,KeyEventArgs e){
   if(e.Key==Key.F1){ShowHelp();e.Handled=true;return;}
   if(e.Key==Key.F5 || (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.R)){e.Handled=true;await RefreshAsync(null);}
   else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.F){tabs.SelectedIndex=0;search.Focus();search.SelectAll();e.Handled=true;}
   else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Enter && tabs.SelectedIndex==0){e.Handled=true;await RenameAsync();}
  }
  private void ResponsiveLayout(){
   bool narrow=Window.ActualWidth<820;
   deviceLayout.ColumnDefinitions[0].Width=new GridLength(narrow?1:0.95,GridUnitType.Star);
   deviceLayout.ColumnDefinitions[1].Width=new GridLength(narrow?0:18);
   deviceLayout.ColumnDefinitions[2].Width=narrow?new GridLength(0):new GridLength(1.3,GridUnitType.Star);
   Grid.SetRow(renameCard,narrow?1:0);Grid.SetColumn(renameCard,narrow?0:2);
   renameCard.Margin=new Thickness(0,narrow?16:0,0,0);deviceList.MaxHeight=narrow?190:Math.Max(180,Math.Min(390,Window.ActualHeight-330));
  }
  private void ShowHelp(){
   MessageBox.Show(Window,"1. Select a paired Bluetooth device.\n2. Enter a new name. If shown, choose Enable saving and approve Windows' administrator prompt.\n3. Choose Save name in the editing window.\n4. Reconnect if Quick Settings still shows the old name.\n\nSaving updates both the current name and the name used after restart. You can save an unchanged name to make an earlier temporary rename persistent.\n\nHistory keeps the previous name and restart setting, with backups on this PC. Older entries from version 1.0 cannot automatically restore the restart setting; enter their previous name manually instead.\n\nClassic Bluetooth devices are supported. Bluetooth LE-only devices are not listed. Names apply on this PC. Unpairing or Windows/driver changes may remove a saved name.\n\nF5 refreshes · Ctrl+F filters · Ctrl+Enter saves · Tab moves between controls.","Using Bluetooth Renamer",MessageBoxButton.OK,MessageBoxImage.Information);
  }
  private void WriteDiagnostics(){
   if(String.IsNullOrEmpty(diagnostics))return;
   try{
    var dpi=VisualTreeHelper.GetDpi(Window);var sb=new StringBuilder();
    sb.AppendLine("ProcessArchitecture="+(Environment.Is64BitProcess?"x64":"x86"));
    sb.AppendLine("WpfDpi="+dpi.PixelsPerInchX+"x"+dpi.PixelsPerInchY);
    sb.AppendLine("WindowDips="+Window.ActualWidth+"x"+Window.ActualHeight);
    sb.AppendLine(DpiSupport.Diagnostics(Window));
    sb.AppendLine("DeviceCount="+devices.Count);
    sb.AppendLine("Status="+status.Text);
    foreach(var d in devices)sb.AppendLine(d.AddressText+" | "+d.Name+" | Connected="+d.Connected);
    File.WriteAllText(diagnostics,sb.ToString());
   }catch{}
  }
 }

 public sealed class PreviewPersistentNameStore:IPersistentNameStore {
  private readonly Dictionary<ulong,byte[]> names=new Dictionary<ulong,byte[]>();
  public byte[] Read(ulong address){byte[] value;return names.TryGetValue(address,out value)?(byte[])value.Clone():null;}
  public void Write(ulong address,byte[] value){if(value==null)names.Remove(address);else names[address]=(byte[])value.Clone();}
 }

 public sealed class PreviewBluetoothService:IBluetoothService {
  private List<DeviceInfo> list=new List<DeviceInfo>{
   new DeviceInfo{Address=0x112233445566,AddressText="11:22:33:44:55:66",Name="Studio Headphones",Connected=true,Kind="Audio"},
   new DeviceInfo{Address=0x223344556677,AddressText="22:33:44:55:66:77",Name="Living Room Speaker",Connected=false,Kind="Audio"},
   new DeviceInfo{Address=0x334455667788,AddressText="33:44:55:66:77:88",Name="Travel Earbuds",Connected=false,Kind="Audio"}};
  private DeviceInfo Copy(DeviceInfo d){return new DeviceInfo{Address=d.Address,AddressText=d.AddressText,Name=d.Name,Connected=d.Connected,Kind=d.Kind,ClassOfDevice=d.ClassOfDevice};}
  public List<DeviceInfo> ListDevices(){return list.Select(Copy).ToList();}
  public DeviceInfo GetDevice(ulong address){return Copy(list.Single(d=>d.Address==address));}
  public DeviceInfo Rename(ulong address,string expectedName,string newName){var d=list.Single(v=>v.Address==address);if(d.Name!=expectedName)throw new InvalidOperationException("The example name changed. Refresh first.");d.Name=newName;return Copy(d);}
 }
}
