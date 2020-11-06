﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ME3ExplorerCore.Gammtek.IO;
using ME3ExplorerCore.Helpers;
using ME3ExplorerCore.Misc;

namespace ME3ExplorerCore.Packages
{
    public static class MEPackageHandler
    {
        static readonly ConcurrentDictionary<string, IMEPackage> openPackages = new ConcurrentDictionary<string, IMEPackage>(StringComparer.OrdinalIgnoreCase);
        public static ObservableCollection<IMEPackage> packagesInTools = new ObservableCollection<IMEPackage>();

        static Func<string, bool, UDKPackage> UDKConstructorDelegate;
        static Func<Stream, string, UDKPackage> UDKStreamConstructorDelegate;

        static Func<string, MEGame, MEPackage> MEConstructorDelegate;
        static Func<Stream, string, MEPackage> MEStreamConstructorDelegate;

        public static void Initialize()
        {
            UDKConstructorDelegate = UDKPackage.RegisterLoader();
            UDKStreamConstructorDelegate = UDKPackage.RegisterStreamLoader();
            MEConstructorDelegate = MEPackage.RegisterLoader();
            MEStreamConstructorDelegate = MEPackage.RegisterStreamLoader();
        }

        public static IReadOnlyList<string> GetOpenPackages() => openPackages.Select(x => x.Key).ToList();

        /// <summary>
        /// Opens a package from a stream. Ensure the position is correctly set to the start of the package.
        /// </summary>
        /// <param name="inStream"></param>
        /// <param name="associatedFilePath"></param>
        /// <param name="useSharedPackageCache"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public static IMEPackage OpenMEPackageFromStream(Stream inStream, string associatedFilePath = null, bool useSharedPackageCache = false, IPackageUser user = null)
        {
            IMEPackage package;
            if (associatedFilePath == null || !useSharedPackageCache)
            {
                package = LoadPackage(inStream, associatedFilePath, false);
            }
            else
            {
                package = openPackages.GetOrAdd(associatedFilePath, fpath =>
                {
                    using (FileStream fs = new FileStream(associatedFilePath, FileMode.Open, FileAccess.Read))
                    {
                        Debug.WriteLine($"Adding package to package cache (Stream): {fpath}");
                        return LoadPackage(fs, fpath, true);
                    }
                });
            }

            if (user != null)
            {
                package.RegisterTool(user);
                addToPackagesInTools(package);
            }
            else
            {
                package.RegisterUse();
            }
            return package;
        }

        /// <summary>
        /// Opens an already open package package, registering it for use in a tool.
        /// </summary>
        /// <param name="package"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public static IMEPackage OpenMEPackage(IMEPackage package, IPackageUser user = null)
        {
            if (user != null)
            {
                package.RegisterTool(user);
                addToPackagesInTools(package);
            }
            else
            {
                package.RegisterUse();
            }
            return package;
        }

        /// <summary>
        /// Opens a Mass Effect package file. By default, this call will attempt to return an existing open (non-disposed) package at the same path if it is opened twice. Use the forceLoadFromDisk parameter to ignore this behavior.
        /// </summary>
        /// <param name="pathToFile">Path to the file to open</param>
        /// <param name="user">????</param>
        /// <param name="forceLoadFromDisk">If the package being opened should skip the shared package cache and forcibly load from disk. </param>
        /// <returns></returns>
        public static IMEPackage OpenMEPackage(string pathToFile, IPackageUser user = null, bool forceLoadFromDisk = false)
        {
            if (File.Exists(pathToFile))
            {
                pathToFile = Path.GetFullPath(pathToFile); //STANDARDIZE INPUT IF FILE EXISTS (it might be a memory file!)
            }

            IMEPackage package;
            if (forceLoadFromDisk)
            {
                using (FileStream fs = new FileStream(pathToFile, FileMode.Open, FileAccess.Read))
                {
                    package = LoadPackage(fs, pathToFile, false);
                }
            }
            else
            {
                package = openPackages.GetOrAdd(pathToFile, fpath =>
                {
                    using (FileStream fs = new FileStream(pathToFile, FileMode.Open, FileAccess.Read))
                    {
                        Debug.WriteLine($"Adding package to package cache (File): {fpath}");
                        return LoadPackage(fs, fpath, true);
                    }
                });
            }



            if (user != null)
            {
                package.RegisterTool(user);
                addToPackagesInTools(package);
            }
            else
            {
                package.RegisterUse();
            }
            return package;
        }

        private static IMEPackage LoadPackage(Stream stream, string filePath = null, bool useSharedCache = false)
        {
            ushort version = 0;
            ushort licenseVersion = 0;
            bool fullyCompressed = false;

            EndianReader er = new EndianReader(stream);
            if (stream.ReadUInt32() == UnrealPackageFile.packageTagBigEndian) er.Endian = Endian.Big;

            // This is stored as integer by cooker as it is flipped by size word in big endian
            uint versionLicenseePacked = er.ReadUInt32();
            if ((versionLicenseePacked == 0x00020000 || versionLicenseePacked == 0x00010000) && er.Endian == Endian.Little && filePath != null) //can only load fully compressed packages from disk since we won't know what the .us file has
            {
                //block size - this is a fully compressed file. we must decompress it
                //for some reason fully compressed files use a little endian package tag
                var usfile = filePath + ".us";
                if (File.Exists(usfile))
                {
                    fullyCompressed = true;
                }
                else if (File.Exists(filePath + ".UNCOMPRESSED_SIZE"))
                {
                    fullyCompressed = true;
                }
            }

            if (!fullyCompressed)
            {
                version = (ushort)(versionLicenseePacked & 0xFFFF);
                licenseVersion = (ushort)(versionLicenseePacked >> 16);
            }


            IMEPackage pkg;
            if (fullyCompressed ||
                (version == MEPackage.ME3UnrealVersion && (licenseVersion == MEPackage.ME3LicenseeVersion || licenseVersion == MEPackage.ME3Xenon2011DemoLicenseeVersion)) ||
                version == MEPackage.ME3WiiUUnrealVersion && licenseVersion == MEPackage.ME3LicenseeVersion ||
                version == MEPackage.ME2UnrealVersion && licenseVersion == MEPackage.ME2LicenseeVersion || //PC and Xbox share this
                version == MEPackage.ME2PS3UnrealVersion && licenseVersion == MEPackage.ME2PS3LicenseeVersion ||
                version == MEPackage.ME2DemoUnrealVersion && licenseVersion == MEPackage.ME2LicenseeVersion ||
                version == MEPackage.ME1UnrealVersion && licenseVersion == MEPackage.ME1LicenseeVersion ||
                version == MEPackage.ME1PS3UnrealVersion && licenseVersion == MEPackage.ME1PS3LicenseeVersion ||
                version == MEPackage.ME1XboxUnrealVersion && licenseVersion == MEPackage.ME1XboxLicenseeVersion)
            {
                stream.Position -= 8; //reset to start
                pkg = MEStreamConstructorDelegate(stream, filePath);
                MemoryAnalyzer.AddTrackedMemoryItem($"MEPackage {Path.GetFileName(filePath)}", new WeakReference(pkg));
            }
            else if (version == 868 || version == 867 && licenseVersion == 0)
            {
                //UDK
                stream.Position -= 8; //reset to start
                pkg = UDKStreamConstructorDelegate(stream, filePath);
                MemoryAnalyzer.AddTrackedMemoryItem($"UDKPackage {Path.GetFileName(filePath)}", new WeakReference(pkg));
            }
            else
            {
                throw new FormatException("Not an ME1, ME2, ME3, or UDK (2015) package file.");
            }

            if (useSharedCache)
            {
                pkg.noLongerUsed += Package_noLongerUsed;
            }

            return pkg;
        }

        public static void CreateAndSavePackage(string path, MEGame game)
        {
            switch (game)
            {
                case MEGame.UDK:
                    UDKConstructorDelegate(path, true).Save();
                    break;
                case MEGame.Unknown:
                    throw new ArgumentException("Cannot create a package file for an Unknown game!", nameof(game));
                default:
                    var package = MEConstructorDelegate(path, game);
                    package.setPlatform(MEPackage.GamePlatform.PC); //Platform must be set or saving code will throw exception (cannot save non-PC platforms)
                    package.Save();
                    break;
            }
        }

        private static void Package_noLongerUsed(UnrealPackageFile sender)
        {
            var packagePath = sender.FilePath;
            if (Path.GetFileNameWithoutExtension(packagePath) != "Core") //Keep Core loaded as it is very often referenced
            {
                if (openPackages.TryRemove(packagePath, out IMEPackage _))
                {
                    Debug.WriteLine($"Released from package cache: {packagePath}");
                }
                else
                {
                    Debug.WriteLine($"Failed to remove package from cache: {packagePath}");
                }
            }
        }

        private static void addToPackagesInTools(IMEPackage package)
        {
            if (!packagesInTools.Contains(package))
            {
                packagesInTools.Add(package);
                package.noLongerOpenInTools += Package_noLongerOpenInTools;
            }
        }

        private static void Package_noLongerOpenInTools(UnrealPackageFile sender)
        {
            IMEPackage package = sender as IMEPackage;
            packagesInTools.Remove(package);
            sender.noLongerOpenInTools -= Package_noLongerOpenInTools;

        }

        public static IMEPackage OpenUDKPackage(string pathToFile, IPackageUser user = null, bool forceLoadFromDisk = false)
        {
            IMEPackage pck = OpenMEPackage(pathToFile, user, forceLoadFromDisk);
            if (pck.Game == MEGame.UDK)
            {
                return pck;
            }

            pck.Release(user);
            throw new FormatException("Not a UDK package file.");
        }

        public static IMEPackage OpenME3Package(string pathToFile, IPackageUser user = null, bool forceLoadFromDisk = false)
        {
            IMEPackage pck = OpenMEPackage(pathToFile, user, forceLoadFromDisk);
            if (pck.Game == MEGame.ME3)
            {
                return pck;
            }

            pck.Release(user);
            throw new FormatException("Not an ME3 package file.");
        }

        public static IMEPackage OpenME2Package(string pathToFile, IPackageUser user = null, bool forceLoadFromDisk = false)
        {
            IMEPackage pck = OpenMEPackage(pathToFile, user, forceLoadFromDisk);
            if (pck.Game == MEGame.ME2)
            {
                return pck;
            }

            pck.Release(user);
            throw new FormatException("Not an ME2 package file.");
        }

        public static IMEPackage OpenME1Package(string pathToFile, IPackageUser user = null, bool forceLoadFromDisk = false)
        {
            IMEPackage pck = OpenMEPackage(pathToFile, user, forceLoadFromDisk);
            if (pck.Game == MEGame.ME1)
            {
                return pck;
            }

            pck.Release(user);
            throw new FormatException("Not an ME1 package file.");
        }

        public static bool IsPackageInUse(string pathToFile) => openPackages.ContainsKey(Path.GetFullPath(pathToFile));

        public static void PrintOpenPackages()
        {
            Debug.WriteLine("Open Packages:");
            foreach (KeyValuePair<string, IMEPackage> package in openPackages)
            {
                Debug.WriteLine(package.Key);
            }
        }

        //useful for scanning operations, where a common set of packages are going to be referenced repeatedly
        public static DisposableCollection<IMEPackage> OpenMEPackages(IEnumerable<string> filePaths)
        {
            return new DisposableCollection<IMEPackage>(filePaths.Select(filePath => OpenMEPackage(filePath)));
        }
    }

    public class DisposableCollection<T> : List<T>, IDisposable where T : IDisposable
    {
        public DisposableCollection() : base() { }
        public DisposableCollection(IEnumerable<T> collection) : base(collection) { }
        public DisposableCollection(int capacity) : base(capacity) { }

        public void Dispose()
        {
            foreach (T disposable in this)
            {
                disposable?.Dispose();
            }
        }
    }
}