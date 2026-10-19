#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class CustomCommandRegistry
    {
        private readonly Dictionary<string, MethodInfo> _commands = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        private bool _hasScanned;

        public void EnsureScanned()
        {
            if (_hasScanned)
            {
                return;
            }

            _hasScanned = true;
            Scan();
        }

        public bool HasCommand(string name)
        {
            EnsureScanned();
            return _commands.ContainsKey(name);
        }

        public string Invoke(string name, string argumentsJson)
        {
            EnsureScanned();
            if (!_commands.TryGetValue(name, out MethodInfo? method))
            {
                throw new CommandFailureException(
                    "CUSTOM_COMMAND_NOT_FOUND",
                    $"Could not find a registered custom command: {name}",
                    false,
                    null);
            }

            try
            {
                object? result = method.Invoke(null, new object[] { argumentsJson });
                return result?.ToString() ?? "{}";
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new CommandFailureException(
                    "CUSTOM_COMMAND_FAILED",
                    $"Custom command failed: {ex.InnerException.Message}",
                    false,
                    ex.InnerException.ToString());
            }
        }

        public string[] GetRegisteredNames()
        {
            EnsureScanned();
            var names = new string[_commands.Count];
            _commands.Keys.CopyTo(names, 0);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private void Scan()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnityCliBridge] Assembly scan failed: {assembly.FullName} - {ex.Message}");
                }
            }

            if (_commands.Count > 0)
            {
                Debug.Log($"[UnityCliBridge] Registered {_commands.Count} custom commands: {string.Join(", ", GetRegisteredNames())}");
            }
        }

        private void ScanAssembly(Assembly assembly)
        {
            string? asmName = assembly.GetName().Name;
            if (asmName == null)
            {
                return;
            }

            if (asmName.StartsWith("System", StringComparison.Ordinal)
                || asmName.StartsWith("Unity", StringComparison.Ordinal)
                || asmName.StartsWith("UnityEngine", StringComparison.Ordinal)
                || asmName.StartsWith("UnityEditor", StringComparison.Ordinal)
                || asmName.StartsWith("mscorlib", StringComparison.Ordinal)
                || asmName.StartsWith("netstandard", StringComparison.Ordinal)
                || asmName.StartsWith("Mono", StringComparison.Ordinal)
                || asmName.StartsWith("Microsoft", StringComparison.Ordinal))
            {
                return;
            }

            foreach (Type type in GetLoadableTypes(assembly))
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    PucCommandAttribute? attr = method.GetCustomAttribute<PucCommandAttribute>();
                    if (attr == null)
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (string.IsNullOrWhiteSpace(attr.Name))
                    {
                        Debug.LogWarning($"[UnityCliBridge] {type.FullName}.{method.Name} has an empty [PucCommand] name.");
                        continue;
                    }

                    if (parameters.Length != 1
                        || parameters[0].ParameterType != typeof(string)
                        || method.ReturnType != typeof(string))
                    {
                        Debug.LogWarning($"[UnityCliBridge] [PucCommand(\"{attr.Name}\")] method {type.FullName}.{method.Name} has the wrong signature. It must be static string Method(string argumentsJson).");
                        continue;
                    }

                    if (_commands.ContainsKey(attr.Name))
                    {
                        Debug.LogWarning($"[UnityCliBridge] Duplicate custom command name: {attr.Name}");
                        continue;
                    }

                    _commands[attr.Name] = method;
                }
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var types = new List<Type>();
                foreach (Type? type in ex.Types)
                {
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }

                return types;
            }
        }
    }
}
