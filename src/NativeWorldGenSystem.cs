using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.WorldBuilding;

namespace VanillaWorldGenCPP {
    internal class NativeWorldGenSystem : ModSystem {

        private class GenerationTaskInfo {
            public GenerationProgress VanillaProgressReporter { get; }
            public GenerationTaskInfo(GenerationProgress progress) => VanillaProgressReporter = progress;
        }

        private static readonly ConcurrentDictionary<int, GenerationTaskInfo> _activeGenTasks = new();
        private static int _nextTaskId = 0;

        // Delegate instance that will be passed to C++.
        private VanillaWorldGenCPP.ProgressReportDelegate _progressDelegateInstance;

        // Handle to pin the delegate in memory, this is needed to avoid GC moving it while in use.
        private GCHandle _progressDelegateHandle;

        public override void Load() {
            _progressDelegateInstance = GlobalCppProgressCallback;
            _progressDelegateHandle = GCHandle.Alloc(_progressDelegateInstance); // Pin
        }

        public override void Unload() {
            if (_progressDelegateHandle.IsAllocated) {
                _progressDelegateHandle.Free();
            }
            _progressDelegateInstance = null;
            _activeGenTasks.Clear();
        }

        private static int GetNextTaskId() => Interlocked.Increment(ref _nextTaskId);

        // New entry point for worldgen, called by IL hook.
        // threadContext is usually a GenerationProgress instance. Typically null.
        public static void CustomWorldGenEntry (object threadContext) {
            var systemInstance = ModContent.GetInstance<NativeWorldGenSystem>();
            var modInstance = ModContent.GetInstance<VanillaWorldGenCPP>();

            // If the mod didn't load properly, we cannot proceed.
            if (modInstance == null || systemInstance == null) {
                Main.statusText = "Error: VanillaWorldGenCPP mod instance not found!";
                return;
            }

            var progress = threadContext as GenerationProgress ?? new GenerationProgress();
            WorldGenerator.CurrentGenerationProgress = progress;

            try {
                progress.TotalWeight = 1.0; // The C++ side reports progress from 0.0 to 1.0.
                progress.Start(1.0);

                SoundEngine.PlaySound(SoundID.MenuOpen);
                SetArchiveLock(true); // Prevent any auto backup.

                modInstance.Logger.Info("Starting native C++ world generation...");
                systemInstance.ExecuteNativeGeneration(progress, modInstance);
            }
            catch (Exception e) {
                progress.Message = "Critical error during world generation!";
                modInstance.Logger.Error($"Exception in WorldGenCallback: {e}");
                // Make sure that the game is not stuck on the generation screen on error.
                if (Main.menuMode == 10 || Main.menuMode == 888) Main.menuMode = 6;
            }
            finally {
                // Ensure cleanup even if an error happened.
                progress.End();
                WorldGen.generatingWorld = false;
                WorldGenerator.CurrentGenerationProgress = null;
                SoundEngine.PlaySound(SoundID.MenuClose);
            }
        }

        // Wrapper to safely set the BackupIO.archiveLock field.
        public static void SetArchiveLock(bool value) {
            var modInstance = VanillaWorldGenCPP.Instance;
            if (modInstance._backupIoArchiveLockField != null) {
                try {
                    modInstance._backupIoArchiveLockField.SetValue(null, value);
                    modInstance.Logger.Debug($"BackupIO.archiveLock set to {value}.");
                }
                catch (Exception ex) {
                    modInstance.Logger.Error($"Failed to set BackupIO.archiveLock to {value}: {ex}");
                }
            }
            else {
                modInstance.Logger.Warn($"Cannot set BackupIO.archiveLock to {value}: FieldInfo not available.");
            }
        }

        // Orchestrates the call to the native library and handles the result.
        private void ExecuteNativeGeneration(GenerationProgress progress, VanillaWorldGenCPP modInstance) {
            int taskId = GetNextTaskId();
            _activeGenTasks.TryAdd(taskId, new GenerationTaskInfo(progress));

            bool success = modInstance.InvokeNativeWorldGen(taskId, _progressDelegateInstance, out int resultCode);

            _activeGenTasks.TryRemove(taskId, out _);

            if (success) {
                modInstance.Logger.Info("C++ worldgen completed successfully. Finalizing world save...");
                progress.Message = "Finalizing and saving world (C++)...";
                FinalizeAndSaveWorld(modInstance);
            }
            else {
                modInstance.Logger.Error($"C++ worldgen failed with exit code: {resultCode}.");
                // TODO: Should add a bit of a wait or maybe create a new UI window? The progress message will go by too fast
                progress.Message = "C++ world generation failed!";
                Main.statusText = "World generation failed. See client.log for details.";
            }

            if (Main.menuMode == 10 || Main.menuMode == 888) Main.menuMode = 6; // Make sure we are not stuck
        }

        private static void FinalizeAndSaveWorld(VanillaWorldGenCPP modInstance) {
            // Save .twld (we don't have modded stuff but other mods might? Not fully sure what's up with this, keeping it anyways).
            try {
                modInstance.Logger.Debug("Saving modded world data (.twld)...");
                object[] parameters = new object[] { Main.worldPathName, Main.ActiveWorldFileData.IsCloudSave };
                modInstance._worldIoSaveMethod.Invoke(null, parameters);
            }
            catch (Exception ex) {
                modInstance.Logger.Error($"Error saving modded world data (.twld): {ex}");
            }

            // Unlock the archive system so it can create a backup.
            SetArchiveLock(false);

            // Manually trigger the world backup.
            try {
                modInstance.Logger.Debug("Archiving world backup...");
                object[] archiveParams = new object[] { Main.worldPathName, Main.ActiveWorldFileData.IsCloudSave };
                modInstance._backupIoWorldArchiveWorldMethod.Invoke(null, archiveParams);
            }
            catch (Exception ex) {
                modInstance.Logger.Error($"Error during world backup: {ex}");
            }
        }

        // Static callback that the C++ library calls to report progress.
        private static void GlobalCppProgressCallback(int taskId, float progress, int pass, string message) {
            if (_activeGenTasks.TryGetValue(taskId, out GenerationTaskInfo taskInfo)) {
                var reporter = taskInfo.VanillaProgressReporter;

                // C++ provides a vanilla pass index for standard pass info (eg "Making the world evil").
                // It also provides a custom message if the pass index is -1.
                reporter.Message = (pass != -1) ? Lang.gen[pass].Value : message;

                // Progress that is -1.0 is a signal for an error message.
                if (progress >= 0) {
                    reporter.Set(progress);
                }
                else {
                    reporter.Message = $"Error from C++: {message}";
                }
            }
        }
    }
}