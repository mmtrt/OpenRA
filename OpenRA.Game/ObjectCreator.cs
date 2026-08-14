#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using OpenRA.Primitives;

namespace OpenRA
{
	public sealed class ObjectCreator : IDisposable
	{
		// .NET does not support unloading assemblies, so mod libraries will leak across mod changes.
		// This tracks the assemblies that have been loaded since game start so that we don't load multiple copies
		static readonly Dictionary<string, Assembly> ResolvedAssemblies = [];

		readonly Cache<string, Type> typeCache;
		readonly Cache<Type, ConstructorInfo> ctorCache;
		readonly (Assembly Assembly, string Namespace)[] assemblies;

		public ObjectCreator(Manifest manifest, InstalledMods mods)
		{
			typeCache = new Cache<string, Type>(FindType);
			ctorCache = new Cache<Type, ConstructorInfo>(GetCtor);

			// Allow mods to load types from the core Game assembly, and any additional assemblies they specify.
			// Assemblies must exist in the game binary directory next to the main game executable.
			var assemblyList = new List<Assembly>() { typeof(Game).Assembly };
			foreach (var filename in manifest.Assemblies)
			LoadAssembly(assemblyList, Path.Combine(Platform.BinDir, filename));

			AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
			assemblies = assemblyList.SelectMany(asm => asm.GetNamespaces().Select(ns => (asm, ns))).ToArray();
		}

			static void LoadAssembly(List<Assembly> assemblyList, string resolvedPath)
		{
			// Prefer already-loaded assemblies (Android ProjectReference / compile-in).
			var simple = Path.GetFileNameWithoutExtension(resolvedPath);
			if (!string.IsNullOrEmpty(simple))
			{
				foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
				{
					try
					{
						if (loaded.GetName().Name == simple)
						{
							assemblyList.Add(loaded);
							return;
						}
					}
					catch
					{
						// Dynamic assemblies may throw on GetName
					}
				}
			}

			if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
				throw new FileNotFoundException(
					"Mod assembly not loaded in memory and not found on disk: " + resolvedPath,
					resolvedPath);

			// .NET doesn't provide any way of querying the metadata of an assembly without either:
			//   (a) loading duplicate data into the application domain, breaking the world.
			//   (b) crashing if the assembly has already been loaded.
			// We can't check the internal name of the assembly, so we'll work off the data instead
			string hash;
			using (var stream = File.OpenRead(resolvedPath))
			hash = CryptoUtil.SHA1Hash(stream);

			if (!ResolvedAssemblies.TryGetValue(hash, out var assembly))
			{
				// IMPORTANT: Use the default ALC so all mod TraitInfo types share
				// a single Type identity (required for TypeDictionary / HasTraitInfo).
				// Isolated AssemblyLoadContext per DLL breaks Chronoshiftable etc.
				assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(resolvedPath);
				ResolvedAssemblies[hash] = assembly;
			}

			assemblyList.Add(assembly);
		}
	}
}
