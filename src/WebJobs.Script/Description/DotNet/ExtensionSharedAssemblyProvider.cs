// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.Azure.WebJobs.Script.Extensibility;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    /// <summary>
    /// An <see cref="ISharedAssemblyProvider"/> that delegates to a collection of <see cref="ScriptBindingProvider"/>s
    /// providing them a chance to resolve assemblies for extensions.
    /// </summary>
    [CLSCompliant(false)]
    public class ExtensionSharedAssemblyProvider : ISharedAssemblyProvider, IDisposable
    {
        private readonly Collection<ScriptBindingProvider> _bindingProviders;
        private readonly Dictionary<string, Assembly> _resolvedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private bool disposedValue = false;

        /// <summary>
        /// Constructs a new instance.
        /// </summary>
        /// <param name="bindingProviders">The collection of <see cref="ScriptBindingProvider"/>s.</param>
        public ExtensionSharedAssemblyProvider(Collection<ScriptBindingProvider> bindingProviders)
        {
            _bindingProviders = bindingProviders;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            Assembly assembly = null;

            AssemblyName name = new AssemblyName(args.Name);
            if (_resolvedAssemblies.TryGetValue(name.Name, out assembly))
            {
                return assembly;
            }

            return null;
        }

        public bool TryResolveAssembly(string assemblyName, out Assembly assembly)
        {
            assembly = null;

            foreach (var bindingProvider in _bindingProviders)
            {
                if (bindingProvider.TryResolveAssembly(assemblyName, out assembly))
                {
                    if (!_resolvedAssemblies.ContainsKey(assemblyName))
                    {
                        _resolvedAssemblies.Add(assemblyName, assembly);
                    }
                    break;
                }
            }

            return assembly != null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
