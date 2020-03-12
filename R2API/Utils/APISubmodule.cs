using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
// ReSharper disable UnusedMember.Global
// ReSharper disable ClassNeverInstantiated.Global

namespace R2API.Utils {
    [Flags]
    internal enum InitStage {
        SetHooks   = 0x01,
        Load       = 0x02,
        Unload     = 0x04,
        UnsetHooks = 0x08
    }

    // ReSharper disable once InconsistentNaming
    [AttributeUsage(AttributeTargets.Class)]
    internal class R2APISubmodule : Attribute {
        public int Build;
    }

    // ReSharper disable once InconsistentNaming
    [AttributeUsage(AttributeTargets.Method)]
    internal class R2APISubmoduleInit : Attribute {
        public InitStage Stage;
    }

    /// <summary>
    /// Attribute to have at the top of your BaseUnityPlugin class if you want to load a specific R2API Submodule.
    /// Parameter(s) are the nameof the submodules.
    /// e.g: [R2APISubmoduleDependency("SurvivorAPI", "ItemAPI")]
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class R2APISubmoduleDependency : Attribute {
        public string[] SubmoduleNames { get; }

        public R2APISubmoduleDependency(params string[] submoduleName) {
            SubmoduleNames = submoduleName;
        }
    }


    // ReSharper disable once InconsistentNaming
    /// <summary>
    /// 
    /// </summary>
    public class APISubmoduleHandler {
        private readonly int _build;
        private readonly ManualLogSource _logger;
        private HashSet<string> _moduleSet;
        private HashSet<string> LoadedModules;

        internal APISubmoduleHandler(int build, ManualLogSource logger = null) {
            _build = build;
            _logger = logger;
        }

        /// <summary>
        /// Return true if the specified submodule is loaded.
        /// </summary>
        /// <param name="submodule">nameof the submodule</param>
        public bool IsLoaded(string submodule) => LoadedModules.Contains(submodule);

        internal void LoadRequested() {
            Assembly[] GetAssemblies() {
                var assemblies = new List<Assembly>();

                var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                while (!path.ToLower().EndsWith("ins")) {
                    path = Directory.GetParent(path).FullName;
                }

                foreach (string dll in Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories)) {
                    var fileName = Path.GetFileName(dll);
                    if (!fileName.ToLower().Contains("r2api") && !fileName.ToLower().Contains("mmhook")) {
                        try // bepis code
                        {
                            assemblies.Add(Assembly.LoadFile(dll));
                        }
                        catch (BadImageFormatException) { } //unmanaged dll
                        catch (ReflectionTypeLoadException) { }
                    }

                }

                return assemblies.ToArray();
            }

            var allTypes = GetAssemblies()
                .SelectMany(assembly => {
                    try {
                        return assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException) {
                        return Enumerable.Empty<Type>();
                    }
                })
                .ToList();

            _moduleSet = new HashSet<string>();

            foreach (var type in allTypes) {
                var subModules = type.GetCustomAttributes<R2APISubmoduleDependency>();
                foreach (var subModule in subModules) {
                    foreach (var name in subModule.SubmoduleNames) {
                        _moduleSet.Add(name);
                    }
                }
            }

            var moduleTypes = Assembly.GetExecutingAssembly().GetTypes().Where(APISubmoduleFilter).ToList();

            foreach (var moduleType in moduleTypes) {
                R2API.Logger.LogInfo($"Enabling R2API Submodule: {moduleType.Name}");
            }
            
            var faults = new Dictionary<Type, Exception>();
            LoadedModules = new HashSet<string>();

            moduleTypes
                .ForEachTry(t => InvokeStage(t, InitStage.SetHooks), faults);
            moduleTypes.Where(t => !faults.ContainsKey(t))
                .ForEachTry(t => InvokeStage(t, InitStage.Load), faults);

            faults.Keys.ForEachTry(t => {
                _logger?.Log(LogLevel.Error, $"{t.Name} could not be initialized and has been disabled:\n\n{faults[t]}");
                InvokeStage(t, InitStage.UnsetHooks);
            });

            moduleTypes.Where(t => !faults.ContainsKey(t))
                .ForEachTry(t => t.SetFieldValue("_loaded", true));
            moduleTypes.Where(t => !faults.ContainsKey(t))
                .ForEachTry(t => LoadedModules.Add(t.Name));
        }


        // ReSharper disable once InconsistentNaming
        private bool APISubmoduleFilter(Type type) {
            var attr = type.GetCustomAttribute<R2APISubmodule>();

            if (!_moduleSet.Contains(type.Name)) {
                return false;
            }

            if (attr == null)
                return false;

            if (attr.Build != default && attr.Build != _build)
                _logger?.Log(LogLevel.Message,
                    $"{type.Name} was built for build {attr.Build}, current build is {_build}.");

            return true;
        }

        private void InvokeStage(Type type, InitStage stage) {
            var method = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.GetCustomAttributes(typeof(R2APISubmoduleInit))
                .Any(a => ((R2APISubmoduleInit) a).Stage.HasFlag(stage))).ToList();

            if (method.Count == 0) {
                _logger?.Log(LogLevel.Debug, $"{type.Name} has no static method registered for {stage.ToString()}");
                return;
            }

            method.ForEach(m => m.Invoke(null, null));
        }
    }


    public static class EnumerableExtensions {
        /// <summary>
        /// ForEach but with a try catch in it.
        /// </summary>
        /// <param name="list">the enumerable object</param>
        /// <param name="action">the action to do on it</param>
        /// <param name="exceptions">the exception dictionary that will get filled, null by default if you simply want to silence the errors if any pop.</param>
        /// <typeparam name="T"></typeparam>
        public static void ForEachTry<T>(this IEnumerable<T> list, Action<T> action, IDictionary<T, Exception> exceptions = null) {
            list.ToList().ForEach(element => {
                try {
                    action(element);
                }
                catch (Exception exception) {
                    exceptions?.Add(element, exception);
                }
            });
        }
    }
}
