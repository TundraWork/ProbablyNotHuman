// Lists the game's methods that read UnityEngine.Input and writes mod/InputReaders.g.cs.
// The mod patches exactly these methods through ModReflect.Target, so it never scans IL at run time.
// Usage: dotnet run --project tools/InputReaders -- "<game>/Probably Stolen_Data/Managed/Assembly-CSharp.dll" mod/InputReaders.g.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Input members VirtualInput can answer; keys are Cecil "name(parameter types)" signatures.
var supported = new HashSet<string>
{
    "GetKey(UnityEngine.KeyCode)", "GetKey(System.String)", "GetKeyDown(UnityEngine.KeyCode)", "GetKeyDown(System.String)",
    "GetKeyUp(UnityEngine.KeyCode)", "GetKeyUp(System.String)", "GetMouseButton(System.Int32)", "GetMouseButtonDown(System.Int32)",
    "GetMouseButtonUp(System.Int32)", "get_mousePosition()", "get_mouseScrollDelta()", "get_anyKey()", "get_anyKeyDown()",
    "GetAxis(System.String)", "GetAxisRaw(System.String)", "GetButton(System.String)", "GetButtonDown(System.String)", "GetButtonUp(System.String)",
};
// ModScanner.GuardTypes plus the rest of the loader UI: patching these is refused.
var guard = new[] { "ModLoader", "ModTrust", "ModTrustRecord", "ModFingerprint", "ModTrustList", "ModScanner", "ModCapability", "ModSecurityDebug",
    "ModSteamRules", "ModSteamGuard", "ModSteamAuth", "ModWorkshop", "SteamManager", "PaperBookRegistry", "ModReflect" };

string Key(MethodReference m) => m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
bool Restricted(ICustomAttributeProvider p) => p.HasCustomAttributes && p.CustomAttributes.Any(a => a.AttributeType.Name == "ModRestrictedAttribute");
TypeDefinition Outermost(TypeDefinition t) { while (t.DeclaringType != null) t = t.DeclaringType; return t; }
bool Guarded(TypeDefinition t)
{
    string name = Outermost(t).Name;
    if (guard.Contains(name) || name.StartsWith("Mod", StringComparison.Ordinal) || name.StartsWith("LoadedMod", StringComparison.Ordinal) || name.StartsWith("Steam", StringComparison.Ordinal)
        || name.IndexOf("Workshop", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Trust", StringComparison.OrdinalIgnoreCase) >= 0) return true;
    for (var x = t; x != null; x = x.DeclaringType) if (Restricted(x)) return true;
    return false;
}
// Runtime type name for ModReflect.FindType: namespace + nested types joined by '+'.
string RuntimeName(TypeReference t) => t.FullName.Replace('/', '+');
// A C# expression producing the runtime Type, or null when the signature cannot be named without computed reflection.
string TypeExpression(TypeReference t)
{
    if (t is ByReferenceType byRef) { string inner = TypeExpression(byRef.ElementType); return inner == null ? null : inner + ".MakeByRefType()"; }
    if (t is ArrayType array && array.Rank == 1) { string inner = TypeExpression(array.ElementType); return inner == null ? null : inner + ".MakeArrayType()"; }
    if (t is GenericInstanceType generic)
    {
        string syntax = CSharpName(generic);
        return syntax == null ? null : "typeof(" + syntax + ")";
    }
    if (t.IsGenericParameter || t is PointerType) return null;
    TypeDefinition definition;
    try { definition = t.Resolve(); } catch (AssemblyResolutionException) { return null; }
    if (definition == null) return null;
    bool visible = true;
    for (var x = definition; x != null; x = x.DeclaringType) visible &= x.IsPublic || x.IsNestedPublic;
    if (definition.Module.Assembly.Name.Name == "Assembly-CSharp" && !visible) return "ModReflect.FindType(\"" + RuntimeName(t) + "\")";
    if (!visible || definition.HasGenericParameters) return null;
    return "typeof(global::" + t.FullName.Replace('/', '.') + ")";
}

// C# syntax for a fully public type, including closed generics such as ISet<KeyCode>; null otherwise.
string CSharpName(TypeReference t)
{
    if (t.IsGenericParameter || t is PointerType || t is ByReferenceType) return null;
    if (t is ArrayType array) { string inner = CSharpName(array.ElementType); return inner == null || array.Rank != 1 ? null : inner + "[]"; }
    TypeDefinition definition;
    try { definition = t.Resolve(); } catch (AssemblyResolutionException) { return null; }
    if (definition == null) return null;
    for (var x = definition; x != null; x = x.DeclaringType) if (!(x.IsPublic || x.IsNestedPublic)) return null;
    string name = "global::" + t.GetElementType().FullName.Replace('/', '.');
    int tick = name.IndexOf('`');
    if (t is GenericInstanceType generic)
    {
        var arguments = generic.GenericArguments.Select(CSharpName).ToList();
        if (tick < 0 || arguments.Any(a => a == null)) return null;
        return name.Substring(0, tick) + "<" + string.Join(", ", arguments) + ">";
    }
    return tick >= 0 ? null : name;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
var targets = new List<string>();
var skipped = new List<string>();
var unsupported = new SortedSet<string>();
foreach (var type in module.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    if (Guarded(type) || type.HasGenericParameters || type.Name == "<Module>") continue;
    foreach (var method in type.Methods)
    {
        if (!method.HasBody || method.HasGenericParameters || Restricted(method)) continue;
        var reads = method.Body.Instructions
            .Where(i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) && i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.Input")
            .Select(i => Key((MethodReference)i.Operand)).Distinct().ToList();
        if (reads.Count == 0) continue;
        string id = RuntimeName(type) + "." + method.Name;
        foreach (string read in reads.Where(r => !supported.Contains(r))) unsupported.Add(id + ": Input." + read);
        if (!reads.Any(supported.Contains)) { skipped.Add(id + " (only unsupported Input members)"); continue; }
        if (method.IsConstructor) { skipped.Add(id + " (constructor; ModReflect.Target resolves methods only)"); continue; }
        var parameters = method.Parameters.Select(p => TypeExpression(p.ParameterType)).ToList();
        if (parameters.Any(p => p == null)) { skipped.Add(id + " (signature not nameable without computed reflection)"); continue; }
        targets.Add($"            new Target(\"{RuntimeName(type)}\", \"{method.Name}\", () => new Type[] {{ {string.Join(", ", parameters)} }}),");
    }
}

var output = new StringBuilder();
output.AppendLine("// <auto-generated> by tools/InputReaders from " + Path.GetFileName(args[0]) + " (" + module.Mvid + "). Regenerate after game updates.");
output.AppendLine("using System;");
output.AppendLine();
output.AppendLine("namespace ProbablyNotHuman");
output.AppendLine("{");
output.AppendLine("    internal static class InputReaders");
output.AppendLine("    {");
output.AppendLine("        internal sealed class Target");
output.AppendLine("        {");
output.AppendLine("            public readonly string Type, Method; public readonly Func<Type[]> Signature;");
output.AppendLine("            public Target(string type, string method, Func<Type[]> signature) { Type = type; Method = method; Signature = signature; }");
output.AppendLine("        }");
output.AppendLine("        internal const string GameModule = \"" + module.Mvid + "\";");
output.AppendLine("        internal static readonly Target[] Targets =");
output.AppendLine("        {");
foreach (string line in targets) output.AppendLine(line);
output.AppendLine("        };");
output.AppendLine("        internal static readonly string[] Skipped =");
output.AppendLine("        {");
foreach (string line in skipped) output.AppendLine("            \"" + line.Replace("\"", "\\\"") + "\",");
output.AppendLine("        };");
output.AppendLine("        internal static readonly string[] UnsupportedReads =");
output.AppendLine("        {");
foreach (string line in unsupported) output.AppendLine("            \"" + line.Replace("\"", "\\\"") + "\",");
output.AppendLine("        };");
output.AppendLine("    }");
output.AppendLine("}");
File.WriteAllText(args[1], output.ToString());
Console.WriteLine($"{targets.Count} targets, {skipped.Count} skipped, {unsupported.Count} unsupported reads -> {args[1]}");
