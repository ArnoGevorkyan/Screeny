using System.Collections.Generic;

namespace ScreenTimeTracker.Models
{
    public static class ProcessFilter
    {
        public static readonly HashSet<string> IgnoredProcesses = new()
        {
            // Windows explorer and UI components
            "explorer",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "ApplicationFrameHost",
            "SystemSettings",
            "TextInputHost",
            "SearchUI",
            "Cortana",
            "SearchApp",
            
            // Terminals and command line
            "WindowsTerminal",
            "cmd",
            "powershell",
            "pwsh",
            "conhost",
            "wt",
            
            // Windows store and system apps
            "WinStore.App",
            "LockApp",
            "LogonUI",
            "UserOOBEBroker",
            "osk", // On-screen keyboard
            
            // Core system processes
            "fontdrvhost",
            "dwm",
            "csrss",
            "services",
            "svchost",
            "taskhostw",
            "ctfmon",
            "rundll32",
            "dllhost",
            "sihost",
            "taskmgr",
            "SecurityHealthSystray",
            "SecurityHealthService",
            "Registry",
            "MicrosoftEdgeUpdate",
            "WmiPrvSE",
            "spoolsv",
            "TabTip",
            "TabTip32",
            "winlogon",
            "wlanext",
            "wuauclt",
            "lsaiso",
            "csrss",
            "wininit",
            "runtimebroker",
            "backgroundTaskHost",
            
            // Windows Defender and Security
            "MsMpEng",
            "SecurityHealthService",
            "SecurityHealthSystray",
            "smartscreen",
            "MpCmdRun",
            "NisSrv",
            "MpSigStub",
            
            // Windows Update and Maintenance
            "TiWorker",
            "UsoClient",
            "WaasMedicAgent",
            "SIHClient",
            "UpdateAssistant",
            "MediaCreationTool",
            "WindowsUpdateBox",
            
            // Background services and drivers
            "igfxEM",
            "igfxHK",
            "igfxTray",
            "igfxCUIService",
            "audiodg",
            "smss",
            "lsass",
            "NVDisplay.Container",
            "nvcontainer",
            "ONENOTEM",
            "SettingSyncHost",
            "uhssvc",
            "WUDFHost",
            "AAM Updates Notifier",
            "CompPkgSrv",
            "PresentationFontCache",
            "SearchIndexer",
            "SgrmBroker",
            "SpeechRuntime",
            "startup",
            "System",
            "SystemSettingsBroker",
            
            // Graphics and Hardware
            "nvtray",
            "nvdisplay.container",
            "RtkAuUService64",
            "RtkAudioService",
            "IAStorIcon",
            "IntelAudioService",
            "AdobeUpdateService",
            
            // Cloud and Sync Services (background only)
            "OneDrive",
            "GoogleDriveFS",
            "Dropbox",
            "iCloudServices",
            "SkypeHost",
            
            // Microsoft Office background services
            "OfficeClickToRun",
            "msoia",
            "MSOSYNC",
            "MicrosoftEdgeUpdate",
            
            // Antivirus background processes (common ones)
            "AvastSvc",
            "avp",
            "avgnt",
            "McShield",
            "nortonsecurity",
            "SentinelAgent",
            "CarbonBlack",
            
            // Self-exclusion
            "Screeny",
            "ScreenTimeTracker",
            
            // Windows tools and dialogs
            "SnippingTool",
            "EaseOfAccessDialog",
            "Magnify", // Windows Magnifier
            "Narrator", // Windows Narrator
            "Notepad", // Basic notepad for quick notes
            "mspaint", // MS Paint
            "charmap", // Character Map
            "calc", // Windows Calculator
            "SoundVolumeView",
            
            // Razer and gaming hardware utilities
            "RazerCortex.Shell",
            "Razer Synapse Service Process",
            "RazerCentralService",
            "RzSDKService",
            "RzChromaStreamServer",
            "RazerIngameEngine",
            "RzActionSvc",
            "Razer Synapse 3 Host",
            "RazerAppEngine",
            
            // Other gaming hardware utilities
            "LogitechGHUB",
            "LGHUB",
            "LGHUBUpdater",
            "CORSAIR iCUE 4 Software",
            "iCUE",
            "CorsairService",
            "SteelSeriesEngine",
            "SSEngine3",
            "NvContainer",
            "NVIDIA Web Helper Service",
            "NVIDIA Display Driver Service",
            "GeForceExperience",
            "NvTelemetryContainer",
            "MSI Afterburner",
            "RTSSHooksLoader64",
            "EVGA Precision X1",
            "HWiNFO64",
            "GPU-Z",
            "CPU-Z",
            
            // RGB and peripheral software
            "SignalRGB",
            "OpenRGB",
            "AuraService",
            "AsusSystemControlInterface",
            "ArmouryCrate.Service",
            "ArmouryCrateService",
            "LightingService",
            
            // Network and connectivity
            "NetworkManager",
            "WifiManager",
            "BluetoothUserService",
            "PhoneExperienceHost",
            
            // Gaming platform launchers/background services
            "VimeWorld",
            "Steam", // Steam when just running in background
            "EpicGamesLauncher",
            "Origin",
            "Battle.net",
            "RiotClientServices",
            
            // System utilities that run briefly
            "msiexec",
            "InstallUtil",
            "MicrosoftEdgeCP",
            "WWAHost",
            "Video.UI",
            "Photos",
            "Movies & TV",
            "Groove Music"
        };
    }
} 