﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AssemblyRewriter
{
	public class AssemblyRewriter
	{
		private readonly bool _verbose;
		private readonly string _keyFile;
		private Dictionary<string, string> _renames = new Dictionary<string, string>();

		public AssemblyRewriter(Options options)
		{
			_verbose = options.Verbose;
			// if a keyfile has been passed but no merge argument, use it to sign the rewritten assembly
			_keyFile = !string.IsNullOrEmpty(options.KeyFile) && !options.Merge ? options.KeyFile : null;
		}

		public void Rewrite(
			IEnumerable<string> inputPaths,
			IEnumerable<string> outputPaths,
			IEnumerable<string> additionalResolveDirectories
		)
		{
			var assemblies = inputPaths.Zip(outputPaths,
				(inputPath, outputPath) => new AssemblyToRewrite(inputPath, outputPath)).ToList();

			_renames = assemblies.ToDictionary(k => k.InputName, v => v.OutputName);

			var resolveDirs = assemblies.Select(a => a.InputDirectory)
				.Concat(assemblies.Select(a => a.OutputDirectory))
				.Concat(additionalResolveDirectories)
				.Distinct();

			var resolver = new AssemblyResolver(resolveDirs);
			var readerParameters = new ReaderParameters {AssemblyResolver = resolver, ReadWrite = true};

			foreach (var assembly in assemblies) RewriteAssembly(assembly, assemblies, readerParameters);
		}

		private string RenameTypeName(string typeName, Func<string, string, string, string> replace = null)
		{
			replace ??= (t, o, n) => t.Replace(o + ".", n + ".");
			foreach (var kv in _renames)
			{
				//safeguard against multiple renames
				if (typeName.StartsWith($"{kv.Value}.") ||
				    typeName.Contains($"<{kv.Value}.") ||
				    typeName.Contains($",{kv.Value}."))
					continue;

				var n = replace(typeName, kv.Key, kv.Value);
				if (typeName != n) return n;
			}

			return typeName;
		}

		private bool IsRewritableType(string typeName) =>
			_renames.Keys.Any(r => typeName.Contains($"{r}."));

		private bool IsRewritableType(Func<string, string, bool> act) =>
			_renames.Any(kv => act(kv.Key, kv.Value));

		private void RewriteAttributes(string assembly, IEnumerable<CustomAttribute> attributes)
		{
			foreach (var attribute in attributes)
			{
				RewriteTypeReference(assembly, attribute.AttributeType);
				RewriteMemberReference(assembly, attribute.Constructor);

				if (attribute.HasConstructorArguments)
				{
					foreach (var constructorArgument in attribute.ConstructorArguments)
					{
						var genericInstanceType = constructorArgument.Value as GenericInstanceType;
						var valueTypeReference = constructorArgument.Value as TypeReference;
						var valueTypeDefinition = constructorArgument.Value as TypeDefinition;
						RewriteTypeReference(assembly, constructorArgument.Type);
						if (valueTypeReference != null) RewriteTypeReference(assembly, valueTypeReference);
						if (genericInstanceType != null) RewriteTypeReference(assembly, genericInstanceType);
						if (valueTypeDefinition == null)
							RewriteTypeReference(assembly, valueTypeDefinition);

						if (constructorArgument.Type.Name == nameof(Type))
						{
							// intentional no-op, but required for Cecil
							// to update the ctor arguments
						}
					}
				}

				if (attribute.HasProperties)
					foreach (var property in attribute.Properties)
						RewriteTypeReference(assembly, property.Argument.Type);

				if (attribute.HasFields)
					foreach (var field in attribute.Fields)
						RewriteTypeReference(assembly, field.Argument.Type);
			}
		}

		private void RewriteMemberReference(string assembly, MemberReference memberReference)
		{
			if (!IsRewritableType(memberReference.Name)) return;

			if (memberReference.DeclaringType != null)
				RewriteTypeReference(assembly, memberReference.DeclaringType);

			var name = RenameTypeName(memberReference.Name);
			Write(assembly, memberReference.GetType().Name, $"{memberReference.Name} to {name}");
			memberReference.Name = name;
		}

		private void RewriteGenericParameter(string assembly, GenericParameter genericParameter)
		{
			var name = RenameTypeName(genericParameter.Name);
			Write(assembly, nameof(GenericParameter), $"{genericParameter.Name} to {name}");
			genericParameter.Name = name;

			foreach (var genericParameterConstraint in genericParameter.Constraints)
			{
				var constraintTypeName = genericParameterConstraint.ConstraintType.Name;
				if (!IsRewritableType(constraintTypeName)) continue;

				name = RenameTypeName(constraintTypeName);
				Write(assembly, nameof(GenericParameterConstraint), $"{constraintTypeName} to {name}");
				genericParameterConstraint.ConstraintType.Name = name;
			}

			foreach (var nestedGenericParameter in genericParameter.GenericParameters)
				RewriteGenericParameter(assembly, nestedGenericParameter);
		}

		private void RewriteAssembly(AssemblyToRewrite assemblyToRewrite, List<AssemblyToRewrite> assembliesToRewrite,
			ReaderParameters readerParameters)
		{
			if (assemblyToRewrite.Rewritten) return;

			string tempOutputPath = null;
			string currentName;
			using (var assembly = AssemblyDefinition.ReadAssembly(assemblyToRewrite.InputPath, readerParameters))
			{
				currentName = assembly.Name.Name;
				var newName = assemblyToRewrite.OutputName;

				Write(currentName, nameof(AssemblyDefinition),
					$"rewriting {currentName} from {assemblyToRewrite.InputPath}");

				foreach (var moduleDefinition in assembly.Modules)
				{
					foreach (var assemblyReference in moduleDefinition.AssemblyReferences)
					{
						Write(currentName, nameof(AssemblyDefinition),
							$"{assembly.Name} references {assemblyReference.Name}");

						var assemblyReferenceToRewrite =
							assembliesToRewrite.FirstOrDefault(a => a.InputName == assemblyReference.Name);

						if (assemblyReferenceToRewrite != null)
						{
							if (!assemblyReferenceToRewrite.Rewritten)
							{
								Write(currentName, nameof(AssemblyNameReference),
									$"{assemblyReference.Name} will be rewritten first");
								RewriteAssembly(assemblyReferenceToRewrite, assembliesToRewrite, readerParameters);
							}
							else
								Write(currentName, nameof(AssemblyNameReference),
									$"{assemblyReference.Name} already rewritten");

							foreach (var innerModuleDefinition in assembly.Modules)
							{
								RewriteTypeReferences(currentName, innerModuleDefinition.GetTypeReferences());
								RewriteTypes(currentName, innerModuleDefinition.Types);
							}

							assemblyReference.Name = assemblyReferenceToRewrite.OutputName;
						}
					}

					RewriteTypes(currentName, moduleDefinition.Types);
					moduleDefinition.Name = RenameTypeName(moduleDefinition.Name);
				}

				RewriteAssemblyTitleAttribute(assembly, currentName, newName);
				assembly.Name.Name = newName;
				var writerParameters = new WriterParameters();

				if (!string.IsNullOrEmpty(_keyFile) && File.Exists(_keyFile))
				{
					Write(currentName, nameof(AssemblyDefinition),
						$"signing {newName} with keyfile {_keyFile}");
					var fileBytes = File.ReadAllBytes(_keyFile);
					writerParameters.StrongNameKeyBlob = fileBytes;
					assembly.Name.Attributes |= AssemblyAttributes.PublicKey;
					assembly.MainModule.Attributes |= ModuleAttributes.StrongNameSigned;
				}

				if (assemblyToRewrite.OutputPath == assemblyToRewrite.InputPath)
				{
					tempOutputPath = assemblyToRewrite.OutputPath + ".temp";
					assembly.Write(tempOutputPath, writerParameters);
					assemblyToRewrite.Rewritten = true;
					Write(currentName, nameof(AssemblyDefinition),
						$"finished rewriting {currentName} into {tempOutputPath}");
				}
				else
				{
					assembly.Write(assemblyToRewrite.OutputPath, writerParameters);
					assemblyToRewrite.Rewritten = true;
					Write(currentName, nameof(AssemblyDefinition),
						$"finished rewriting {currentName} into {assemblyToRewrite.OutputPath}");
				}
			}

			if (!string.IsNullOrWhiteSpace(tempOutputPath))
			{
				File.Delete(assemblyToRewrite.OutputPath);
				File.Move(tempOutputPath, assemblyToRewrite.OutputPath);
				Write(currentName, nameof(AssemblyDefinition),
					$"Rename {tempOutputPath} back to {assemblyToRewrite.OutputPath}");
			}
		}

		private void RewriteTypeReferences(string assembly, IEnumerable<TypeReference> typeReferences)
		{
			foreach (var typeReference in typeReferences)
				RewriteTypeReference(assembly, typeReference);
		}

		private void RewriteTypeReference(string assembly, TypeReference typeReference)
		{
			//var oReference = typeReference;
//            var doNotRewrite = IsRewritableType((o, n) =>
//                (!oReference.Namespace.StartsWith(o) || oReference.Namespace.StartsWith(n)) &&
//                (oReference.Namespace != string.Empty || !oReference.Name.StartsWith($"<{o}-"))
//            );
//
//            if (doNotRewrite) return;
			if (typeReference == null) return;

			if (typeReference is TypeSpecification) typeReference = typeReference.GetElementType();

			if (typeReference == null) return;

			if (typeReference.Namespace != string.Empty && IsRewritableType((o, n) =>
				(typeReference.Namespace == o || typeReference.Namespace.StartsWith($"{o}.")) &&
				!typeReference.Namespace.StartsWith(n)))
			{
				var name = RenameTypeName(typeReference.Namespace, (t, o, n) => t.Replace(o, n));
				var newFullName = RenameTypeName(typeReference.FullName, (t, o, n) => t.Replace(o + ".", n + "."));
				Write(assembly, nameof(TypeReference), $"{typeReference.FullName} to {newFullName}");
				typeReference.Namespace = name;
			}

			if (IsRewritableType((o, n) => typeReference.Name.StartsWith($"<{o}-")))
			{
				var name = RenameTypeName(typeReference.Name, (t, o, n) => t.Replace($"<{o}-", $"<{n}-"));
				var newFullName = RenameTypeName(typeReference.FullName, (t, o, n) => t.Replace($"<{o}-", $"<{n}-"));
				Write(assembly, nameof(TypeReference), $"{typeReference.FullName} to {newFullName}");
				typeReference.Name = name;
			}

			if (typeReference.HasGenericParameters)
				foreach (var genericParameter in typeReference.GenericParameters)
					RewriteGenericParameter(assembly, genericParameter);

			if (typeReference.DeclaringType != null)
				RewriteTypeReference(assembly, typeReference.DeclaringType);
		}

		private void RewriteAssemblyTitleAttribute(AssemblyDefinition assembly, string currentName, string newName)
		{
			foreach (var attribute in assembly.CustomAttributes)
			{
				if (attribute.AttributeType.Name != nameof(AssemblyTitleAttribute)) continue;

				var currentAssemblyName = (string) attribute.ConstructorArguments[0].Value;
				var newAssemblyName = Regex.Replace(currentAssemblyName, Regex.Escape(currentName), newName);

				// give the assembly a new title, even when the top level namespace is not part of it
				if (newAssemblyName == currentAssemblyName)
					newAssemblyName += $" ({newName})";

				Write(assembly.Name.Name, nameof(AssemblyTitleAttribute),
					$"{currentAssemblyName} to {newAssemblyName}");
				attribute.ConstructorArguments[0] =
					new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, newAssemblyName);
			}
		}

		private void RewriteTypes(string assembly, IEnumerable<TypeDefinition> typeDefinitions)
		{
			foreach (var typeDefinition in typeDefinitions)
			{
				if (typeDefinition.HasNestedTypes)
					RewriteTypes(assembly, typeDefinition.NestedTypes);

				if (IsRewritableType((o, n) =>
					(typeDefinition.Namespace == o || typeDefinition.Namespace.StartsWith($"{o}.")) &&
					!typeDefinition.Namespace.StartsWith(n)))
				{
					var name = RenameTypeName(typeDefinition.Namespace, (t, o, n) => t.Replace(o, n));
					Write(assembly, nameof(TypeDefinition),
						$"{typeDefinition.FullName} to {name}.{typeDefinition.Name}");
					typeDefinition.Namespace = name;
				}

				RewriteAttributes(assembly, typeDefinition.CustomAttributes);

				foreach (var methodDefinition in typeDefinition.Methods)
					RewriteMethodDefinition(assembly, methodDefinition);

				foreach (var propertyDefinition in typeDefinition.Properties)
				{
					RewriteAttributes(assembly, propertyDefinition.CustomAttributes);
					RewriteTypeReference(assembly, propertyDefinition.PropertyType);
					RewriteMemberReference(assembly, propertyDefinition);

					if (propertyDefinition.GetMethod != null)
						RewriteMethodDefinition(assembly, propertyDefinition.GetMethod);
					if (propertyDefinition.SetMethod != null)
						RewriteMethodDefinition(assembly, propertyDefinition.SetMethod);
					if (propertyDefinition.HasOtherMethods)
						foreach (var otherMethod in propertyDefinition.OtherMethods)
							RewriteMethodDefinition(assembly, otherMethod);

					// generic properties or explicitly implemented interface properties
					if (IsRewritableType(propertyDefinition.Name))
					{
						var name = RenameTypeName(propertyDefinition.Name);
						Write(assembly, nameof(PropertyDefinition), $"{propertyDefinition.Name} to {name}");
						propertyDefinition.Name = name;
					}
				}

				foreach (var fieldDefinition in typeDefinition.Fields)
				{
					// compiler generated backing field
					if (IsRewritableType((fieldDefinition.Name)))
					{
						var name = RenameTypeName(fieldDefinition.Name);
						Write(assembly, nameof(PropertyDefinition), $"{fieldDefinition.Name} to {name}");
						fieldDefinition.Name = name;
					}

					RewriteAttributes(assembly, fieldDefinition.CustomAttributes);
					RewriteTypeReference(assembly, fieldDefinition.FieldType);
					RewriteMemberReference(assembly, fieldDefinition);
				}

				foreach (var interfaceImplementation in typeDefinition.Interfaces)
				{
					RewriteAttributes(assembly, interfaceImplementation.CustomAttributes);
					RewriteTypeReference(assembly, interfaceImplementation.InterfaceType);
					RewriteMemberReference(assembly, interfaceImplementation.InterfaceType);
				}

				foreach (var eventDefinition in typeDefinition.Events)
				{
					RewriteAttributes(assembly, eventDefinition.CustomAttributes);
					RewriteTypeReference(assembly, eventDefinition.EventType);
					RewriteMemberReference(assembly, eventDefinition.EventType);
				}

				foreach (var genericParameter in typeDefinition.GenericParameters)
				{
					RewriteAttributes(assembly, genericParameter.CustomAttributes);
					RewriteTypeReference(assembly, genericParameter);
					RewriteGenericParameter(assembly, genericParameter);
				}
			}
		}

		private void RewriteMethodDefinition(string assembly, MethodDefinition methodDefinition)
		{
			RewriteAttributes(assembly, methodDefinition.CustomAttributes);
			RewriteMemberReference(assembly, methodDefinition);

			if (IsRewritableType((o, n) => methodDefinition.Name.StartsWith(o + ".")))
			{
				var name = RenameTypeName(methodDefinition.Name);
				Write(assembly, nameof(MethodDefinition), $"{methodDefinition.Name} to {name}");
				methodDefinition.Name = name;
			}

			foreach (var methodDefinitionOverride in methodDefinition.Overrides)
			{
				// explicit interface implementation of generic interface
				if (IsRewritableType(methodDefinition.Name))
				{
					var name = RenameTypeName(methodDefinition.Name);
					Write(assembly, nameof(MethodDefinition), $"{methodDefinition.Name} to {name}");
					methodDefinition.Name = name;
				}

				foreach (var genericParameter in methodDefinitionOverride.GenericParameters)
				{
					RewriteAttributes(assembly, genericParameter.CustomAttributes);
					RewriteGenericParameter(assembly, genericParameter);
				}

				RewriteMemberReference(assembly, methodDefinitionOverride);
			}

			foreach (var genericParameter in methodDefinition.GenericParameters)
			{
				RewriteAttributes(assembly, genericParameter.CustomAttributes);
				RewriteGenericParameter(assembly, genericParameter);
			}

			foreach (var parameterDefinition in methodDefinition.Parameters)
			{
				RewriteAttributes(assembly, parameterDefinition.CustomAttributes);
				RewriteTypeReference(assembly, parameterDefinition.ParameterType);
			}

			RewriteTypeReference(assembly, methodDefinition.ReturnType);
			RewriteMethodBody(assembly, methodDefinition);
		}

		private void RewriteMethodBody(string assembly, MethodDefinition methodDefinition)
		{
			if (!methodDefinition.HasBody) return;

			for (var index = 0; index < methodDefinition.Body.Instructions.Count; index++)
			{
				var instruction = methodDefinition.Body.Instructions[index];

				// Strings that reference the namespace
				if (instruction.OpCode.Code == Code.Ldstr)
				{
					var operandString = (string) instruction.Operand;
					if (IsRewritableType((o, n) => operandString.StartsWith($"{o}.")))
					{
						var name = RenameTypeName(operandString);
						Write(assembly, nameof(Instruction), $"{instruction.OpCode.Code}. {name}");
						instruction.Operand = operandString;
					}
				}
				// loading or storing compiler generated backing fields
				else if (instruction.OpCode.Code == Code.Ldfld || instruction.OpCode.Code == Code.Stfld)
				{
					var fieldReference = (FieldReference) instruction.Operand;

					// rename the compiler backing field name
					if (IsRewritableType((o, n) => fieldReference.Name.StartsWith($"<{o}.")))
					{
						var name = RenameTypeName(fieldReference.Name, (t, o, n) => t.Replace($"<{o}.", $"<{n}."));
						Write(assembly, nameof(Instruction), $"{instruction.OpCode.Code}. {name}");
						fieldReference.Name = name;
					}

					RewriteMemberReference(assembly, fieldReference);
					RewriteTypeReference(assembly, fieldReference.FieldType);
				}
				// method calls
				else if (instruction.OpCode.Code == Code.Call)
				{
					var methodReference = (MethodReference) instruction.Operand;
					RewriteMemberReference(assembly, methodReference);
					RewriteTypeReference(assembly, methodReference.ReturnType);

					if (methodReference.IsGenericInstance)
					{
						var genericInstance = (GenericInstanceMethod) methodReference;
						RewriteTypeReferences(assembly, genericInstance.GenericArguments);
					}
				}
			}
		}

		private void Write(string assembly, string operation, string message)
		{
			void Write()
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write($"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.ffzzz}][");
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.Write(assembly.PadRight(18));
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write("][");
				Console.ForegroundColor = ConsoleColor.Green;
				Console.Write($"{operation.PadRight(23)}");
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write("] ");
				Console.ForegroundColor = ConsoleColor.White;
				Console.WriteLine($"{message}");
				Console.ResetColor();
			}

			switch (operation)
			{
				case nameof(AssemblyDefinition):
				case nameof(AssemblyNameReference):
				case nameof(AssemblyTitleAttribute):
				case nameof(Rewrite):
					Write();
					break;
				default:
					if (_verbose)
						Write();
					break;
			}
		}
	}
}
