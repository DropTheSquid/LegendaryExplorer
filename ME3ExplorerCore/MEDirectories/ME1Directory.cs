﻿using System;
using System.Collections.Generic;
using System.IO;
using ME3ExplorerCore.Misc;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace ME3ExplorerCore.MEDirectories
{
    public static class ME1Directory
    {

        private static string _gamePath;
        public static string gamePath
        {
            get
            {
                if (string.IsNullOrEmpty(_gamePath))
                    return null;
                return Path.GetFullPath(_gamePath); //normalize
            }
            set
            {
                if (value != null)
                {
                    if (value.Contains("BioGame"))
                        value = value.Substring(0, value.LastIndexOf("BioGame"));
                }
                _gamePath = value;
            }
        }
        public static string BioGamePath => gamePath != null ? Path.Combine(gamePath, @"BioGame\") : null;
        public static string cookedPath => gamePath != null ? Path.Combine(gamePath, @"BioGame\CookedPC\") : "Not Found"; //Should this return Not found instead of null?
        public static string DLCPath => gamePath != null ? Path.Combine(gamePath, @"DLC\") : "Not Found"; //Should this return Not found instead of null?

        // "C:\...\MyDocuments\BioWare\Mass Effect\" folder
        public static string BioWareDocPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), @"\BioWare\Mass Effect\");
        public static string GamerSettingsIniFile => Path.Combine(BioWareDocPath, @"BIOGame\Config\GamerSettings.ini");
        public static string ExecutablePath => gamePath != null ? Path.Combine(gamePath, @"Binaries\MassEffect.exe") : null;

        static ME1Directory()
        {
            if (!string.IsNullOrEmpty(CoreLibSettings.Instance.ME1Directory))
            {
                gamePath = CoreLibSettings.Instance.ME1Directory;
            }
            else
            {
#if WINDOWS
                string hkey32 = @"HKEY_LOCAL_MACHINE\SOFTWARE\";
                string hkey64 = @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\";
                string subkey = @"BioWare\Mass Effect";

                string keyName = hkey32 + subkey;
                string test = (string)Registry.GetValue(keyName, "Path", null);
                if (test != null)
                {
                    gamePath = test;
                    CoreLibSettings.Instance.ME1Directory = gamePath;
                    return;
                }

                keyName = hkey64 + subkey;
                gamePath = (string)Registry.GetValue(keyName, "Path", null);
                if (gamePath != null)
                {
                    gamePath += Path.DirectorySeparatorChar;
                    CoreLibSettings.Instance.ME1Directory = gamePath;
                    return;
                }
#endif
            }
        }

        public static CaseInsensitiveDictionary<string> OfficialDLCNames = new CaseInsensitiveDictionary<string>
        {
            ["DLC_UNC"] = "Bring Down the Sky",
            ["DLC_Vegas"] = "Pinnacle Station"
        };

        public static List<string> OfficialDLC = new List<string>
        {
            "DLC_UNC",
            "DLC_Vegas"
        };
    }
}
