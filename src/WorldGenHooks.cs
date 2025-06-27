using MonoMod.Cil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace VanillaWorldGenCPP {
    public class WorldGenHooks : ILoadable {

        private ILHook _worldGenCallbackHook;

        public void Load(Mod mod) {
            try {
                // Find the original method TML uses to start worldgen in a new background thread
                MethodInfo originalMethod = typeof(WorldGen).GetMethod(
                    "worldGenCallback",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                );

                if (originalMethod == null) {
                    mod.Logger.Error("Failed to find MethodInfo for Terraria.WorldGen.worldGenCallback. Custom world generation will not be used.");
                    return;
                }

                // Create the hook. This completely overrides all genPasses, this will NOT work with modded passes!
                _worldGenCallbackHook = new ILHook(originalMethod, RedirectWorldGenCallback);
                mod.Logger.Info("Applied ILHook to Terraria.WorldGen.worldGenCallback.");
            }
            catch (Exception e) {
                mod.Logger.Error($"Error applying ILHook to Terraria.WorldGen.worldGenCallback: {e}");
            }
        }

        public void Unload() {
            _worldGenCallbackHook?.Dispose();
            _worldGenCallbackHook = null;
        }

        private void RedirectWorldGenCallback(ILContext il) {
            var cursor  = new ILCursor(il);
            var mod = ModContent.GetInstance<VanillaWorldGenCPP>();

            // Find the static C# method in the ModSystem that will act as the new entry point for worldgen
            var customCallbackMethod = typeof(NativeWorldGenSystem).GetMethod(nameof(NativeWorldGenSystem.CustomWorldGenEntry));
            if (customCallbackMethod == null) {
                mod?.Logger.Error("ILHook Error: Could not find NativeWorldGenSystem.CustomWorldGenEntry method!");
                return;
            }

            // Clear original instructions
            il.Body.Instructions.Clear();

            // The original method signature is `void worldGenCallback(object threadContext)`
            // `threadContext` is usually a `GenerationProgress` object. We need to pass it to our new method
            cursor.Emit(OpCodes.Ldarg_0);

            // Call new static method
            cursor.Emit(OpCodes.Call, customCallbackMethod);

            // Properly return
            cursor.Emit(OpCodes.Ret);

            mod?.Logger.Debug("Replaced Terraria.WorldGen.worldGenCallback body with a call to NativeWorldGenSystem.CustomWorldGenEntry.");
        }
    }
}
