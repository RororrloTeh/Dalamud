using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Internal.DesignSystem;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.AutoUpdate;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Utility;

namespace Dalamud.Interface.Internal.Windows.Settings.Tabs;

// REGION TODO: 国服 Dalamud 修改
[SuppressMessage(
    "StyleCop.CSharp.DocumentationRules",
    "SA1600:Elements should be documented",
    Justification = "Internals")]
internal class SettingsTabPlugin : SettingsTab
{
    public override SettingsEntry[] Entries => [];

    public override string Title => "插件";

    public override SettingsOpenKind Kind => SettingsOpenKind.Plugin;

    private DalamudConfiguration Config => Service<DalamudConfiguration>.Get();
    
    private List<ThirdPartyRepoSettings> thirdRepoList = [];
    private bool                         thirdRepoListChanged;
    private string                       thirdRepoTempUrl  = string.Empty;
    private string                       thirdRepoAddError = string.Empty;
    
    private List<DevPluginLocationSettings> devPluginLocations = [];
    private bool                            devPluginLocationsChanged;
    private string                          devPluginTempLocation     = string.Empty;
    private string                          devPluginLocationAddError = string.Empty;
    private FileDialogManager               fileDialogManager         = new();
    
    private AutoUpdateBehavior         behavior;
    private bool                       updateDisabledPlugins;
    private bool                       checkPeriodically;
    private bool                       chatNotification;
    private string                     pickerSearch          = string.Empty;
    private List<AutoUpdatePreference> autoUpdatePreferences = [];
    
    public override void OnClose()
    {
        this.thirdRepoList      = [.. Service<DalamudConfiguration>.Get().ThirdRepoList.Select(x => x.Clone())];
        this.devPluginLocations = [.. Service<DalamudConfiguration>.Get().DevPluginLoadLocations.Select(x => x.Clone())];
    }

    public override void Load()
    {
        this.thirdRepoList        = [.. Service<DalamudConfiguration>.Get().ThirdRepoList.Select(x => x.Clone())];
        this.thirdRepoListChanged = false;
        
        this.devPluginLocations        = [.. Service<DalamudConfiguration>.Get().DevPluginLoadLocations.Select(x => x.Clone())];
        this.devPluginLocationsChanged = false;
        
        this.behavior              = Config.AutoUpdateBehavior ?? AutoUpdateBehavior.None;
        this.updateDisabledPlugins = Config.UpdateDisabledPlugins;
        this.chatNotification      = Config.SendUpdateNotificationToChat;
        this.checkPeriodically     = Config.CheckPeriodicallyForUpdates;
        this.autoUpdatePreferences = Config.PluginAutoUpdatePreferences;
    }

    public override void Save()
    {
        Config.ThirdRepoList = [.. this.thirdRepoList.Select(x => x.Clone())];
        if (this.thirdRepoListChanged)
        {
            _                         = Service<PluginManager>.Get().SetPluginReposFromConfigAsync(true);
            this.thirdRepoListChanged = false;
        }
        
        Config.DevPluginLoadLocations = [.. this.devPluginLocations.Select(x => x.Clone())];
        if (this.devPluginLocationsChanged)
        {
            _                              = Service<PluginManager>.Get().ScanDevPluginsAsync();
            this.devPluginLocationsChanged = false;
        }
        
        Config.AutoUpdateBehavior           = this.behavior;
        Config.UpdateDisabledPlugins        = this.updateDisabledPlugins;
        Config.SendUpdateNotificationToChat = this.chatNotification;
        Config.CheckPeriodicallyForUpdates  = this.checkPeriodically;
        Config.PluginAutoUpdatePreferences  = this.autoUpdatePreferences;
    }

    public override void PostDraw()
    {
        this.fileDialogManager.Draw();
    }

    public override void Draw()
    {
        using var tab = ImRaii.TabBar("插件##Dalamud.Settings.Tabs.Plugin.Tab");

        using (var generalItem = ImRaii.TabItem("一般##Dalamud.Settings.Tabs.Plugin.Tab.General"))
        {
            if (generalItem)
                DrawGeneralTabItem();
        }
        
        using (var autoUpdateItem = ImRaii.TabItem("自动更新##Dalamud.Settings.Tabs.Plugin.Tab.AutoUpdate"))
        {
            if (autoUpdateItem)
                DrawAutoUpdateTabItem();
        }
        
        using (var thirdRepoItem = ImRaii.TabItem("第三方插件##Dalamud.Settings.Tabs.Plugin.Tab.ThirdRepo"))
        {
            if (thirdRepoItem)
                DrawThirdRepoTabItem();
        }
        
        using (var localPluginItem = ImRaii.TabItem("本地插件##Dalamud.Settings.Tabs.Plugin.Tab.LocalPlugin"))
        {
            if (localPluginItem)
                DrawLocalPluginTabItem();
        }
    }
    
    private static bool ValidThirdPartyRepoUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
           && (uriResult.Scheme == Uri.UriSchemeHttps || uriResult.Scheme == Uri.UriSchemeHttp);
    
    private static bool ValidDevPluginPath(string path)
        => Path.IsPathRooted(path) && Path.GetExtension(path) == ".dll";
    
    private void AddDevPlugin()
    {
        this.devPluginTempLocation = this.devPluginTempLocation.Trim('"');
        if (this.devPluginLocations.Any(
                r => string.Equals(r.Path, this.devPluginTempLocation, StringComparison.InvariantCultureIgnoreCase)))
        {
            this.devPluginLocationAddError = "[路径已存在]";
            Task.Delay(5000).ContinueWith(_ => this.devPluginLocationAddError = string.Empty);
        }
        else if (!ValidDevPluginPath(this.devPluginTempLocation))
        {
            this.devPluginLocationAddError = "[路径无效]";
            Task.Delay(5000).ContinueWith(_ => this.devPluginLocationAddError = string.Empty);
        }
        else
        {
            this.devPluginLocations.Add(
                new DevPluginLocationSettings
                {
                    Path      = this.devPluginTempLocation,
                    IsEnabled = true,
                });
            this.devPluginLocationsChanged = true;
            this.devPluginTempLocation     = string.Empty;
        }
    }

    private void DrawGeneralTabItem()
    {
        var mainRepoUrl = Config.MainRepoUrl;
        var useSoilPluginManager = Config.UseSoilPluginManager;

        ImGui.Text("默认主库");
        
        if (ImGui.RadioButton("国服 (Daily Routines)", mainRepoUrl == PluginRepository.MainRepoUrlSoil))
        {
            Config.MainRepoUrl = PluginRepository.MainRepoUrlSoil;
            Config.QueueSave();
            
            _ = Service<PluginManager>.Get().SetPluginReposFromConfigAsync(true);
        }
        
        ImGui.SameLine();
        if (ImGui.RadioButton("国际服 (goatcorp)", mainRepoUrl == PluginRepository.MainRepoUrlGoatCorp))
        {
            Config.MainRepoUrl = PluginRepository.MainRepoUrlGoatCorp;
            Config.QueueSave();
            
            _ = Service<PluginManager>.Get().SetPluginReposFromConfigAsync(true);
        }
        
        ImGuiHelpers.ScaledDummy(15f);

        ImGui.Text("自定义主库链接");
            
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, "修改主库链接, 如果不清楚什么是主库，那就不应该修改下面内容");
        
        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("###CustomMainRepo", ref mainRepoUrl, 1024);
            
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            if (string.IsNullOrWhiteSpace(mainRepoUrl))
                mainRepoUrl = PluginRepository.MainRepoUrlSoil;
                
            Config.MainRepoUrl = mainRepoUrl;
            Config.QueueSave();
                
            _ = Service<PluginManager>.Get().SetPluginReposFromConfigAsync(true);
        }
        
        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.Separator();
        
        ImGuiHelpers.ScaledDummy(15f);

        ImGui.Text("插件库排序方式");

        if (ImGui.RadioButton("使用库分类", useSoilPluginManager))
        {
            Config.UseSoilPluginManager = true;
            Config.QueueSave();

            _ = Service<PluginManager>.Get().ReloadPluginMastersAsync();
        }

        if (ImGui.RadioButton("使用默认分类", !useSoilPluginManager))
        {
            Config.UseSoilPluginManager = false;
            Config.QueueSave();

            _ = Service<PluginManager>.Get().ReloadPluginMastersAsync();
        }

        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.Separator();
        
        ImGuiHelpers.ScaledDummy(15f);
        
        var receiveTest = Config.DoPluginTest;
        if (ImGui.Checkbox("接收插件测试版本", ref receiveTest))
        {
            Config.DoPluginTest = receiveTest;
            Config.QueueSave();
        }
        
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, 
                                 "接收选定插件的测试版本。启用后, 当选定插件有可用的测试版更新时, 会提示更新。\n" +
                                 "在插件安装器中右键选定插件, 选择 \"接收插件测试版\" 即可为该插件启用接收测试版。");
        
        ImGuiHelpers.ScaledDummy(15f);

        ImGui.TextColoredWrapped(ImGuiColors.DalamudRed,
                                 "测试版插件可能会出现不稳定、崩溃、数据丢失等情况, 请自行承担使用风险。");
        
        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.Separator();
        
        ImGuiHelpers.ScaledDummy(15f);

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Eraser, "清除已隐藏插件"))
        {
            Config.HiddenPluginInternalName.Clear();
            Config.QueueSave();
        }
        
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, "清除插件安装器中所有已被隐藏的插件。");
        
        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.Separator();
        
        ImGuiHelpers.ScaledDummy(15f);
        
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey,
                                 "Dalamud 与插件总内存占用: " + Util.FormatBytes(GC.GetTotalMemory(false)));
        
        ImGuiHelpers.ScaledDummy(15f);
    }

    private void DrawThirdRepoTabItem()
    {
        using var id = ImRaii.PushId("thirdRepo"u8);

        ImGui.Text("第三方插件仓库");
        if (this.thirdRepoListChanged)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, "[已修改]");
        }

        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey,
                                 "添加第三方插件仓库，可能导致数据丢失、游戏崩溃等，请自行承担使用风险。");

        ImGuiHelpers.ScaledDummy(15f);

        ThirdPartyRepoSettings repoToRemove = null;
        using (var table = ImRaii.Table("ThirdRepoTable", 4, ImGuiTableFlags.Borders   |
                                                             ImGuiTableFlags.RowBg     |
                                                             ImGuiTableFlags.Resizable |
                                                             ImGuiTableFlags.NoKeepColumnsVisible))
        {
            if (table)
            {
                ImGui.TableSetupColumn("#",  ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("链接", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 40 * ImGuiHelpers.GlobalScale);

                ImGui.TableHeadersRow();

                var repoNumber = 1;
                foreach (var thirdRepoSetting in this.thirdRepoList)
                {
                    ImGui.TableNextRow();
                    using var repoId = ImRaii.PushId(thirdRepoSetting.Url);

                    ImGui.TableNextColumn();
                    var repoNumberStr = repoNumber.ToString();
                    var posX          = ImGui.GetCursorPosX() + ((ImGui.GetColumnWidth() - ImGui.CalcTextSize(repoNumberStr).X) / 2f);
                    ImGui.SetCursorPosX(posX);
                    ImGui.Text(repoNumberStr);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var url = thirdRepoSetting.Url;
                    if (ImGui.InputText("##thirdRepoInput", ref url, 65535, ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        var contains = this.thirdRepoList.Select(repo => repo.Url).Contains(url);
                        if (thirdRepoSetting.Url == url)
                        {
                            // 无变化
                        }
                        else if (contains)
                        {
                            this.thirdRepoAddError = "[仓库已经存在]";
                            Task.Delay(5000).ContinueWith(_ => this.thirdRepoAddError = string.Empty);
                        }
                        else if (!ValidThirdPartyRepoUrl(url))
                        {
                            this.thirdRepoAddError = "[仓库链接无效]";
                            Task.Delay(5000).ContinueWith(_ => this.thirdRepoAddError = string.Empty);
                        }
                        else
                        {
                            thirdRepoSetting.Url      = url;
                            this.thirdRepoListChanged = true;
                        }
                    }

                    ImGui.TableNextColumn();
                    var isEnabled = thirdRepoSetting.IsEnabled;
                    var checkPosX = ImGui.GetCursorPosX() + ((ImGui.GetContentRegionAvail().X - (24 * ImGuiHelpers.GlobalScale)) / 2);
                    ImGui.SetCursorPosX(checkPosX);
                    if (ImGui.Checkbox("##thirdRepoCheck", ref isEnabled))
                    {
                        thirdRepoSetting.IsEnabled = isEnabled;
                        this.thirdRepoListChanged  = true;
                    }

                    ImGui.TableNextColumn();
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                        repoToRemove = thirdRepoSetting;

                    repoNumber++;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiHelpers.CenteredText(repoNumber.ToString());

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##thirdRepoUrlInput", ref this.thirdRepoTempUrl, 300);

                ImGui.TableNextColumn();

                ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(this.thirdRepoTempUrl) && ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
                {
                    this.thirdRepoTempUrl = this.thirdRepoTempUrl.TrimEnd();
                    if (this.thirdRepoList.Any(r => string.Equals(r.Url, this.thirdRepoTempUrl, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        this.thirdRepoAddError = "[仓库已经存在]";
                        Task.Delay(5000).ContinueWith(_ => this.thirdRepoAddError = string.Empty);
                    }
                    else if (!ValidThirdPartyRepoUrl(this.thirdRepoTempUrl))
                    {
                        this.thirdRepoAddError = "[仓库链接无效]";
                        Task.Delay(5000).ContinueWith(_ => this.thirdRepoAddError = string.Empty);
                    }
                    else
                    {
                        this.thirdRepoList.Add(new ThirdPartyRepoSettings
                        {
                            Url       = this.thirdRepoTempUrl,
                            IsEnabled = true,
                        });
                        this.thirdRepoListChanged = true;
                        this.thirdRepoTempUrl     = string.Empty;
                    }
                }
            }
        }
        
        if (repoToRemove != null)
        {
            this.thirdRepoList.Remove(repoToRemove);
            this.thirdRepoListChanged = true;
        }

        if (!string.IsNullOrEmpty(this.thirdRepoAddError))
            ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, this.thirdRepoAddError);
    }

    private void DrawLocalPluginTabItem()
    {
        using var id = ImRaii.PushId("devPluginLocation"u8);

        ImGui.Text("本地插件");

        if (this.devPluginLocationsChanged)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, "[已修改]");
        }

        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey,
                                 "添加本地插件，必须为 .dll 文件。");

        ImGuiHelpers.ScaledDummy(15f);
        
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Folder, "选择本地插件 (.dll)"))
        {
            this.fileDialogManager.OpenFileDialog(
                "选择本地插件 (.dll)",
                ".dll",
                (result, path) =>
                {
                    if (result)
                    {
                        this.devPluginTempLocation = path;
                        this.AddDevPlugin();
                    }
                });
        }
        
        DevPluginLocationSettings locationToRemove = null;
        using (var table = ImRaii.Table("DevPluginTable", 
                                        4, 
                                        ImGuiTableFlags.Borders   | 
                                        ImGuiTableFlags.RowBg     | 
                                        ImGuiTableFlags.Resizable | 
                                        ImGuiTableFlags.NoKeepColumnsVisible))
        {
            if (table)
            {
                ImGui.TableSetupColumn("#",  ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("路径", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 40 * ImGuiHelpers.GlobalScale);

                ImGui.TableHeadersRow();

                var locNumber = 1;

                foreach (var devPluginLocationSetting in this.devPluginLocations)
                {
                    using var localID = ImRaii.PushId(devPluginLocationSetting.Path);
                    
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var locNumberStr = locNumber.ToString();
                    var posX         = ImGui.GetCursorPosX() + ((ImGui.GetColumnWidth() - ImGui.CalcTextSize(locNumberStr).X) / 2f);
                    ImGui.SetCursorPosX(posX);
                    ImGui.Text(locNumberStr);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var path = devPluginLocationSetting.Path;
                    if (ImGui.InputText("##devPluginLocationInput", ref path, 65535, ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        var contains = this.devPluginLocations.Select(loc => loc.Path).Contains(path);
                        if (devPluginLocationSetting.Path == path)
                        {
                            // 无变化
                        }
                        else if (contains)
                        {
                            this.devPluginLocationAddError = "[路径已存在]";
                            Task.Delay(5000).ContinueWith(_ => this.devPluginLocationAddError = string.Empty);
                        }
                        else if (!ValidDevPluginPath(path))
                        {
                            this.devPluginLocationAddError = "[路径无效]";
                            Task.Delay(5000).ContinueWith(_ => this.devPluginLocationAddError = string.Empty);
                        }
                        else
                        {
                            devPluginLocationSetting.Path  = path;
                            this.devPluginLocationsChanged = true;
                        }
                    }

                    ImGui.TableNextColumn();
                    var isEnabled = devPluginLocationSetting.IsEnabled;
                    var checkPosX = ImGui.GetCursorPosX() + ((ImGui.GetContentRegionAvail().X - (24 * ImGuiHelpers.GlobalScale)) / 2);
                    ImGui.SetCursorPosX(checkPosX);
                    if (ImGui.Checkbox("##devPluginLocationCheck", ref isEnabled))
                    {
                        devPluginLocationSetting.IsEnabled = isEnabled;
                        this.devPluginLocationsChanged     = true;
                    }

                    ImGui.TableNextColumn();
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                        locationToRemove = devPluginLocationSetting;

                    locNumber++;
                }

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var lastNumStr = locNumber.ToString();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((ImGui.GetColumnWidth() - ImGui.CalcTextSize(lastNumStr).X) / 2f));
                ImGui.Text(lastNumStr);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##devPluginLocationInputNew", ref this.devPluginTempLocation, 300);

                ImGui.TableNextColumn();

                ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(this.devPluginTempLocation) && ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
                {
                    this.AddDevPlugin();
                }
            }
        }

        if (locationToRemove != null)
        {
            this.devPluginLocations.Remove(locationToRemove);
            this.devPluginLocationsChanged = true;
        }

        if (!string.IsNullOrEmpty(this.devPluginLocationAddError))
            ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, this.devPluginLocationAddError);
    }

    private void DrawAutoUpdateTabItem()
    {
        ImGui.TextColoredWrapped(ImGuiColors.DalamudWhite, Loc.Localize("DalamudSettingsAutoUpdateHint",
                                                "Dalamud 可以自动更新你的插件，确保你始终" +
                                                "能获得最新功能和错误修复。可以在此设置自动更新的时机和方式。"));
        ImGuiHelpers.ScaledDummy(2);

        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsAutoUpdateDisclaimer1",
                                                "你始终可以通过插件列表中的更新按钮手动更新插件。" +
                                                "也可右键单击插件并选择\"始终自动更新\"来为特定插件启用自动更新。"));
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsAutoUpdateDisclaimer2",
                                                "Dalamud 只会在你处于空闲状态时通知更新。"));

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColoredWrapped(ImGuiColors.DalamudWhite, Loc.Localize("DalamudSettingsAutoUpdateBehavior",
                                                "当游戏启动时..."));
        var behaviorInt = (int)this.behavior;
        ImGui.RadioButton(Loc.Localize("DalamudSettingsAutoUpdateNone", "不自动检查更新"), ref behaviorInt, (int)AutoUpdateBehavior.None);
        ImGui.RadioButton(Loc.Localize("DalamudSettingsAutoUpdateNotify", "仅通知新更新"), ref behaviorInt, (int)AutoUpdateBehavior.OnlyNotify);
        ImGui.RadioButton(Loc.Localize("DalamudSettingsAutoUpdateMainRepo", "自动更新主库插件"), ref behaviorInt, (int)AutoUpdateBehavior.UpdateMainRepo);
        ImGui.RadioButton(Loc.Localize("DalamudSettingsAutoUpdateAll", "自动更新所有插件"), ref behaviorInt, (int)AutoUpdateBehavior.UpdateAll);
        this.behavior = (AutoUpdateBehavior)behaviorInt;

        if (this.behavior == AutoUpdateBehavior.UpdateAll)
        {
            var warning = Loc.Localize(
                "DalamudSettingsAutoUpdateAllWarning",
                "警告：这将更新所有插件，包括非主库来源的插件。\n" +
                "这些更新未经 Dalamud 团队审核，可能包含恶意代码。");
            ImGui.TextColoredWrapped(ImGuiColors.DalamudOrange, warning);
        }

        ImGuiHelpers.ScaledDummy(8);

        ImGui.Checkbox(Loc.Localize("DalamudSettingsAutoUpdateDisabledPlugins", "自动更新当时被禁用的插件"), ref this.updateDisabledPlugins);
        ImGui.Checkbox(Loc.Localize("DalamudSettingsAutoUpdateChatMessage", "在聊天栏显示可用更新通知"), ref this.chatNotification);
        ImGui.Checkbox(Loc.Localize("DalamudSettingsAutoUpdatePeriodically", "游戏运行时定期检查更新"), ref this.checkPeriodically);
        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsAutoUpdatePeriodicallyHint",
                                                "启动后不会自动更新插件，仅在你未活跃游戏时接收通知。"));

        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(5);

        ImGui.TextColoredWrapped(ImGuiColors.DalamudWhite, Loc.Localize("DalamudSettingsAutoUpdateOptedIn",
                                                "插件单独设置"));

        ImGui.TextColoredWrapped(ImGuiColors.DalamudWhite, Loc.Localize("DalamudSettingsAutoUpdateOverrideHint",
                                                "在此可为特定插件单独设置是否接收更新，" +
                                                "这将覆盖上述全局设置。"));

        if (this.autoUpdatePreferences.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(20);

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGuiHelpers.CenteredText(Loc.Localize("DalamudSettingsAutoUpdateOptedInHint2",
                                                       "你尚未为任何插件配置自动更新规则"));
            }

            ImGuiHelpers.ScaledDummy(2);
        }
        else
        {
            ImGuiHelpers.ScaledDummy(5);

            var pic = Service<PluginImageCache>.Get();

            var windowSize = ImGui.GetWindowSize();
            var pluginLineHeight = 32 * ImGuiHelpers.GlobalScale;
            Guid? wantRemovePluginGuid = null;

            foreach (var preference in this.autoUpdatePreferences)
            {
                var pmPlugin = Service<PluginManager>.Get().InstalledPlugins
                                                   .FirstOrDefault(x => x.EffectiveWorkingPluginId == preference.WorkingPluginId);

                var btnOffset = 2;

                if (pmPlugin != null)
                {
                    var cursorBeforeIcon = ImGui.GetCursorPos();
                    pic.TryGetIcon(pmPlugin, pmPlugin.Manifest, pmPlugin.IsThirdParty, out var icon, out _);
                    icon ??= pic.DefaultIcon;

                    ImGui.Image(icon.Handle, new Vector2(pluginLineHeight));

                    if (pmPlugin.IsDev)
                    {
                        ImGui.SetCursorPos(cursorBeforeIcon);
                        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.7f))
                        {
                            ImGui.Image(pic.DevPluginIcon.Handle, new Vector2(pluginLineHeight));
                        }
                    }

                    ImGui.SameLine();

                    var text = $"{pmPlugin.Name}{(pmPlugin.IsDev ? " (开发版插件" : string.Empty)}";
                    var textHeight = ImGui.CalcTextSize(text);
                    var before = ImGui.GetCursorPos();

                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (pluginLineHeight / 2) - (textHeight.Y / 2));
                    ImGui.Text(text);

                    ImGui.SetCursorPos(before);
                }
                else
                {
                    ImGui.Image(pic.DefaultIcon.Handle, new Vector2(pluginLineHeight));
                    ImGui.SameLine();

                    var text = Loc.Localize("DalamudSettingsAutoUpdateOptInUnknownPlugin", "未知插件");
                    var textHeight = ImGui.CalcTextSize(text);
                    var before = ImGui.GetCursorPos();

                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (pluginLineHeight / 2) - (textHeight.Y / 2));
                    ImGui.Text(text);

                    ImGui.SetCursorPos(before);
                }

                ImGui.SameLine();
                ImGui.SetCursorPosX(windowSize.X - (ImGuiHelpers.GlobalScale * 320));
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (pluginLineHeight / 2) - (ImGui.GetFrameHeight() / 2));

                string OptKindToString(AutoUpdatePreference.OptKind kind)
                {
                    return kind switch
                    {
                        AutoUpdatePreference.OptKind.NeverUpdate => Loc.Localize("DalamudSettingsAutoUpdateOptInNeverUpdate", "从不更新"),
                        AutoUpdatePreference.OptKind.AlwaysUpdate => Loc.Localize("DalamudSettingsAutoUpdateOptInAlwaysUpdate", "总是更新"),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                }

                ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 250);
                using (var combo = ImRaii.Combo($"###autoUpdateBehavior{preference.WorkingPluginId}", OptKindToString(preference.Kind)))
                {
                    if (combo.Success)
                    {
                        foreach (var kind in Enum.GetValues<AutoUpdatePreference.OptKind>())
                        {
                            if (ImGui.Selectable(OptKindToString(kind)))
                            {
                                preference.Kind = kind;
                            }
                        }
                    }
                }

                ImGui.SameLine();
                ImGui.SetCursorPosX(windowSize.X - (ImGuiHelpers.GlobalScale * 30 * btnOffset) - 5);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (pluginLineHeight / 2) - (ImGui.GetFrameHeight() / 2));

                if (ImGuiComponents.IconButton($"###removePlugin{preference.WorkingPluginId}", FontAwesomeIcon.Trash))
                {
                    wantRemovePluginGuid = preference.WorkingPluginId;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.Localize("DalamudSettingsAutoUpdateOptInRemove", "移除规则"));
            }

            if (wantRemovePluginGuid != null)
            {
                this.autoUpdatePreferences.RemoveAll(x => x.WorkingPluginId == wantRemovePluginGuid);
            }
        }

        void OnPluginPicked(LocalPlugin plugin)
        {
            var id = plugin.EffectiveWorkingPluginId;
            if (id == Guid.Empty)
                throw new InvalidOperationException("Plugin ID is empty.");

            this.autoUpdatePreferences.Add(new AutoUpdatePreference(id));
        }

        bool IsPluginDisabled(LocalPlugin plugin)
            => this.autoUpdatePreferences.Any(x => x.WorkingPluginId == plugin.EffectiveWorkingPluginId);

        bool IsPluginFiltered(LocalPlugin plugin)
            => !plugin.IsDev;

        var pickerId = DalamudComponents.DrawPluginPicker(
            "###autoUpdatePicker", ref this.pickerSearch, OnPluginPicked, IsPluginDisabled, IsPluginFiltered);

        const FontAwesomeIcon addButtonIcon = FontAwesomeIcon.Plus;
        var addButtonText = Loc.Localize("DalamudSettingsAutoUpdateOptInAdd", "添加规则");
        ImGuiHelpers.CenterCursorFor(ImGuiComponents.GetIconButtonWithTextWidth(addButtonIcon, addButtonText));
        if (ImGuiComponents.IconButtonWithText(addButtonIcon, addButtonText))
        {
            this.pickerSearch = string.Empty;
            ImGui.OpenPopup(pickerId);
        }
    }
}
