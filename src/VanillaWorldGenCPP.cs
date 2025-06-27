using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;
using System.Runtime.Intrinsics.X86;

namespace VanillaWorldGenCPP {
    public class VanillaWorldGenCPP : Mod {

        // A delegate matching the signature of the C++ progress reporting callback function.
        // This allows the C++ code to call back into C# to update the world generation UI.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProgressReportDelegate(
            Int32 taskId, // An identifier for the generation task, in case multiple are running (unused).
            float progress, // The overall progress, from 0.0f to 1.0f.
            Int32 pass, // The vanilla generation pass index. -1 if message is custom.
            [MarshalAs(UnmanagedType.LPUTF8Str)] string message // A custom status message from the C++ library.
        );

        // Delegates for the C++ functions we want to call. The signatures must match the exported C++ functions.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateSessionDelegate(int seed, int taskId, int worldSizeEnum, int evilType, int gameMode, ProgressReportDelegate callback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RunWorldGenOnSessionDelegate(
            IntPtr sessionHandle, // Handle to the session.
            [MarshalAs(UnmanagedType.LPUTF8Str)] string saveDir, // Where the world will be saved.
            [MarshalAs(UnmanagedType.LPUTF8Str)] string worldName // For saving the world name to the file properly.
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroySessionDelegate(IntPtr sessionHandle);

        private IntPtr _nativeLibraryHandle = IntPtr.Zero;
        private CreateSessionDelegate _createSession;
        private RunWorldGenOnSessionDelegate _runWorldGenOnSession;
        private DestroySessionDelegate _destroySession;

        // Reflection is used to access internal/private members that are not exposed publicy.
        public FieldInfo _backupIoArchiveLockField ;
        public MethodInfo _worldIoSaveMethod ;
        public MethodInfo _backupIoWorldArchiveWorldMethod ;

        // Needed to use the most optimal native library based on CPU features/OS
        private class LibraryVariant {
            public string Suffix { get; }
            public Func<bool> IsSupported { get; }

            public LibraryVariant(string suffix, Func<bool> isSupported) {
                Suffix = suffix;
                IsSupported = isSupported;
            }
        }

        // List of available library variants. Most to least performant.
        // The first one that is supported by the users CPU will be loaded.
        private static readonly LibraryVariant[] s_libraryVariants = {
            new("avx512", () => Avx512F.IsSupported && Avx512DQ.IsSupported && Avx512BW.IsSupported && Avx512Vbmi.IsSupported),
            new("avx2",   () => Avx2.IsSupported),
            new("x64",    () => true) // Generic x64 fallback, always supported on 64-bit systems.
        };

        internal static VanillaWorldGenCPP Instance { get; private set; }

        public override void Load() {
            Instance = this;

            // On load, clean up any old library files left over from the previous session.
            CleanupOldFiles();

            // We need to determine what libary to load (OS + CPU features).
            string fileExtension = GetNativeLibraryExtension();
            if (string.IsNullOrEmpty(fileExtension)) {
                Logger.Error($"Unsupported OS Platform: {RuntimeInformation.OSDescription}. Cannot load native library.");
                return;
            }

            string chosenLibrarySuffix = SelectOptimalLibraryVariant();
            if (chosenLibrarySuffix == null) {
                Logger.Error("Could not find a supported native library variant for this CPU.");
                return;
            }

            // Now we need to extract the chosen library from the .tmod archive to a temporary directory.
            string libraryName = $"TerrariaSeed_{chosenLibrarySuffix}{fileExtension}";

            string tempLibraryPath;
            try {
                tempLibraryPath = ExtractNativeLibrary(libraryName);
                if (string.IsNullOrEmpty(tempLibraryPath)) return; // Error already logged by the method.
            }
            catch (Exception e) {
                Logger.Error($"Failed to extract native library '{libraryName}'.", e);
                return;
            }

            // Load the library and get pointers to exported functions.
            try {
                _nativeLibraryHandle = NativeLibrary.Load(tempLibraryPath);

                _createSession = GetExport<CreateSessionDelegate>("create_session");
                _runWorldGenOnSession = GetExport<RunWorldGenOnSessionDelegate>("run_worldgen_on_session");
                _destroySession = GetExport<DestroySessionDelegate>("destroy_session");

                if (_createSession == null || _runWorldGenOnSession == null || _destroySession == null) {
                    throw new DllNotFoundException("Failed to find one or more required session functions in the native library.");
                }

                Logger.Info("Native library loaded and functions imported successfully.");

                // Use reflection to get access to the internal methods for saving.
                LoadReflectionMembers();
            }
            catch (Exception e) {
                Logger.Error("Exception during native library load or initialization!", e);
                Unload(); // Attempt a cleanup on failure.
            }

        }

        public override void Unload() {
            Logger.Info("--- VWGCPP Unload() Starting ---");
            if (_nativeLibraryHandle != IntPtr.Zero) {
                NativeLibrary.Free(_nativeLibraryHandle);
                _nativeLibraryHandle = IntPtr.Zero;
            }

            // Clear all delegates and fields to ensure a clean state.
            _createSession = null;
            _runWorldGenOnSession = null;
            _destroySession = null;
            _backupIoArchiveLockField  = null;
            _worldIoSaveMethod  = null;
            _backupIoWorldArchiveWorldMethod  = null;

            Instance = null;
            Logger.Info("--- VWGCPP Unload() Finished ---");
        }

        // Invokes the native C++ worldgen process.
        public bool InvokeNativeWorldGen(int taskId, ProgressReportDelegate progressCallback, out int resultCode) {
            resultCode = -1; // Default error code
            IntPtr sessionHandle = IntPtr.Zero;

            if (_createSession == null) {
                Logger.Error("Native function delegates are not loaded. Cannot start world generation.");
                return false;
            }

            try {
                // Prepare parameters for the C++ library.
                int seed = Main.ActiveWorldFileData.Seed;
                // C++ Expects an enum: 0=Small, 1=Medium, 2=Large.
                int worldSizeType = Main.maxTilesX switch { 4200 => 0, 6400 => 1, 8400 => 2, _ => 0 };
                // C++ Expects: 0=Random, 1=Corruption, 2=Crimson.
                int evilType = WorldGen.WorldGenParam_Evil switch { -1 => 0, 0 => 1, 1 => 2, _ => 0 };
                string worldSaveDirectory = Main.worldPathName;
                string worldName = Main.ActiveWorldFileData.Name;
                int gameMode = Main.GameMode;

                // Create the C++ session object. This will initilize the generator with the current world parameters.
                sessionHandle = _createSession(seed, taskId, worldSizeType, evilType, gameMode, progressCallback);
                if (sessionHandle == IntPtr.Zero) {
                    throw new Exception("The native 'create_session' function returned a null handle.");
                }
                Logger.Debug($"Created native session for TaskId {taskId}. Handle: {sessionHandle}");

                // Run the worldgen.
                resultCode = _runWorldGenOnSession(sessionHandle, worldSaveDirectory, worldName);

                return resultCode == 0;
            }
            catch (Exception ex) {
                Logger.Error("An exception occurred during the managed world generation call.", ex);
                return false;
            }
            finally {
                // CRITICAL: Always destroy the C++ sessions to free its memory, regardless of success or failure.
                if (sessionHandle != IntPtr.Zero) {
                    _destroySession(sessionHandle);
                    Logger.Debug("Destroyed native session.");
                }
            }
        }

        private T GetExport<T>(string name) where T : Delegate {
            IntPtr address = NativeLibrary.GetExport(_nativeLibraryHandle, name);
            if (address == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private void CleanupOldFiles() {
            string tempDir  = Path.Combine(Path.GetTempPath(), "tModLoaderMods", Name, "NativeLibs");

            if (!Directory.Exists(tempDir )) {
                return;
            }

            // Note: Not doing anything recursive as I do NOT want to accidently delete anything else.
            try {
                string[] oldLibraryFiles = Directory.GetFiles(tempDir , "TerrariaSeed*"); // Any .dll/.so/.pdb/etc
                foreach (string oldDllFile in oldLibraryFiles) {
                    try {
                        File.Delete(oldDllFile);
                    }
                    catch (IOException ex) // Could be locked for some reason
                    {
                        Logger.Warn($"Could not delete old temporary native library '{oldDllFile}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) {
                Logger.Warn($"Could not clean up old temporary library directory. This is usually not a problem. Error: {ex.Message}");
            }
        }

        private void LoadReflectionMembers() {
            try {
                Assembly tmlAssembly = typeof(ModLoader).Assembly;

                if (tmlAssembly == null) {
                    Logger.Error("Could not obtain tModLoader's core assembly. Cannot access any other members.");
                    return; // Critical failure.
                }

                Type backupIOType = tmlAssembly.GetType("Terraria.ModLoader.BackupIO");
                Type worldIOType = tmlAssembly.GetType("Terraria.ModLoader.IO.WorldIO");

                // Get Field: BackupIO.archiveLock.
                if (backupIOType != null) {
                    _backupIoArchiveLockField  = backupIOType.GetField(
                        "archiveLock",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    );

                    if (_backupIoArchiveLockField  == null) {
                        Logger.Warn("Failed to get FieldInfo for Terraria.ModLoader.BackupIO.archiveLock. Backup functionality might be affected.");
                    }
                    else {
                        Logger.Debug("Successfully obtained FieldInfo for BackupIO.archiveLock.");
                    }

                    // Get Method: BackupIO.World.ArchiveWorld(string, bool).
                    Type backupIOWorldType = backupIOType.GetNestedType("World", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (backupIOWorldType != null) {
                        _backupIoWorldArchiveWorldMethod = backupIOWorldType.GetMethod(
                            "ArchiveWorld",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                            null,
                            new Type[] { typeof(string), typeof(bool) },
                            null
                        );

                        if (_backupIoWorldArchiveWorldMethod == null) {
                            Logger.Warn("Failed to find Method 'BackupIO.World.ArchiveWorld'. World backup will be disabled.");
                        }
                        else {
                            Logger.Debug("Successfully obtained reflection info for BackupIO.World.ArchiveWorld.");
                        }
                    }
                    else {
                        Logger.Warn("Could not find nested Type 'BackupIO.World'. World backup will be disabled.");
                    }
                }
                else {
                    Logger.Warn("Could not find Type 'Terraria.ModLoader.BackupIO'. World backup functionality will be disabled.");
                }

                // Get Method: WorldIO.Save(string, bool).
                if (worldIOType != null) {
                    _worldIoSaveMethod = worldIOType.GetMethod(
                        "Save",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new Type[] { typeof(string), typeof(bool) },
                        null
                    );

                    if (_worldIoSaveMethod == null) {
                        Logger.Warn("Failed to find Method 'WorldIO.Save(string, bool)'. Saving mod data (.twld) will fail.");
                    }
                    else {
                        Logger.Debug("Successfully obtained reflection info for WorldIO.Save.");
                    }
                }
                else {
                    Logger.Warn("Could not find Type 'Terraria.ModLoader.IO.WorldIO'. Saving mod data (.twld) will fail.");
                }

            }
            catch (Exception ex) {
                Logger.Error("An unexpected exception occurred during reflection setup.", ex);
            }
        }

        private string GetNativeLibraryExtension() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ".dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return ".so";
            // if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return ".dylib";
            return null;
        }

        private string SelectOptimalLibraryVariant() {
            foreach (var variant in s_libraryVariants) {
                if (variant.IsSupported()) {
                    Logger.Info($"CPU supports '{variant.Suffix}'. Loading this native library variant.");
                    return variant.Suffix;
                }
            }
            return null;
        }

        private string ExtractNativeLibrary(string libraryFileName) {
            string internalPath = $"lib/{libraryFileName}";
            if (!FileExists(internalPath)) {
                Logger.Error($"Optimal native library '{internalPath}' not found within the .tmod package. The mod will not function.");
                return null;
            }

            byte[] libraryBytes = GetFileBytes(internalPath);
            if (libraryBytes == null || libraryBytes.Length == 0) {
                Logger.Error($"Failed to get bytes for '{internalPath}', or the file is empty.");
                return null;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "tModLoaderMods", Name, "NativeLibs");
            Directory.CreateDirectory(tempDir); // Might already exist, that's fine.

            string tempLibraryPath = Path.Combine(tempDir, libraryFileName);
            File.WriteAllBytes(tempLibraryPath, libraryBytes);
            Logger.Debug($"Native Library extracted to: {tempLibraryPath}");

            return tempLibraryPath;
        }
        
    }
}