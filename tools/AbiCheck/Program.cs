// Checks that a plugin finds every Shoko.Abstractions type and member it uses in other builds of Shoko.Abstractions.
// A server whose build lacks one still installs the plugin, because every 6.0 build reports the same abstraction
// version, and then fails to load it. Usage: AbiCheck <plugin.dll> <Shoko.Abstractions.dll>...
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AbiCheck <plugin.dll> <Shoko.Abstractions.dll>...");
    return 2;
}

var (types, members) = ReadUsed(args[0]);
Console.WriteLine($"{Path.GetFileName(args[0])} uses {types.Count} types and {members.Count} members from Shoko.Abstractions.");

var failed = false;
foreach (var path in args.Skip(1))
{
    var (haveTypes, haveMembers) = ReadDefined(path);
    var missing = types.Where(type => !haveTypes.Contains(type)).Select(type => $"type   {type}")
        .Concat(members.Where(member => !haveMembers.Contains(member)).Select(member => $"member {member}"))
        .ToList();
    Console.WriteLine($"{path}: {(missing.Count == 0 ? "all found" : $"{missing.Count} missing")}");
    foreach (var line in missing)
        Console.WriteLine($"  {line}");
    failed |= missing.Count > 0;
}

return failed ? 1 : 0;

static (SortedSet<string> Types, SortedSet<string> Members) ReadUsed(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var names = new Names(md);

    var abstractions = md.AssemblyReferences
        .Where(handle => md.GetString(md.GetAssemblyReference(handle).Name) == "Shoko.Abstractions")
        .Cast<AssemblyReferenceHandle?>()
        .FirstOrDefault() ?? throw new InvalidOperationException($"{path} does not reference Shoko.Abstractions.");

    bool FromAbstractions(EntityHandle handle)
    {
        while (handle.Kind == HandleKind.TypeReference)
            handle = md.GetTypeReference((TypeReferenceHandle)handle).ResolutionScope;
        return handle.Kind == HandleKind.AssemblyReference && (AssemblyReferenceHandle)handle == abstractions;
    }

    var types = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var handle in md.TypeReferences)
        if (FromAbstractions(handle))
            types.Add(Names.Of(md, handle));

    var members = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var handle in md.MemberReferences)
    {
        var member = md.GetMemberReference(handle);
        var owner = member.Parent;
        if (owner.Kind == HandleKind.TypeSpecification)
        {
            // A member of a generic type is referenced through its instantiation: GENERICINST (CLASS|VALUETYPE) type ...
            var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)owner).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                continue;
            blob.ReadByte();
            owner = blob.ReadTypeHandle();
        }
        if (owner.Kind != HandleKind.TypeReference || !FromAbstractions(owner))
            continue;
        members.Add($"{Names.Of(md, (TypeReferenceHandle)owner)}::{names.Member(md.GetString(member.Name), member.Signature)}");
    }

    return (types, members);
}

static (HashSet<string> Types, HashSet<string> Members) ReadDefined(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    var names = new Names(md);

    var types = new HashSet<string>(StringComparer.Ordinal);
    var members = new HashSet<string>(StringComparer.Ordinal);
    foreach (var handle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(handle);
        var name = Names.Of(md, handle);
        types.Add(name);
        foreach (var methodHandle in type.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            members.Add($"{name}::{names.Member(md.GetString(method.Name), method.Signature)}");
        }
        foreach (var fieldHandle in type.GetFields())
        {
            var field = md.GetFieldDefinition(fieldHandle);
            members.Add($"{name}::{names.Member(md.GetString(field.Name), field.Signature)}");
        }
    }
    foreach (var handle in md.ExportedTypes)
        types.Add(Names.Of(md, handle));

    return (types, members);
}

/// <summary>Spells types and member signatures the same way whether they are read from a reference or a definition.</summary>
sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public string Member(string name, BlobHandle signature)
    {
        var decoder = new SignatureDecoder<string, object?>(this, md, null);
        var blob = md.GetBlobReader(signature);
        var header = md.GetBlobReader(signature).ReadSignatureHeader();
        if (header.Kind == SignatureKind.Field)
            return $"{name}: {decoder.DecodeFieldSignature(ref blob)}";
        var method = decoder.DecodeMethodSignature(ref blob);
        var arity = method.GenericParameterCount > 0 ? $"``{method.GenericParameterCount}" : "";
        var isStatic = header.IsInstance ? "" : "static ";
        return $"{isStatic}{name}{arity}({string.Join(", ", method.ParameterTypes)}): {method.ReturnType}";
    }

    public static string Of(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{Of(md, (TypeReferenceHandle)type.ResolutionScope)}/{md.GetString(type.Name)}"
            : Qualify(md.GetString(type.Namespace), md.GetString(type.Name));
    }

    public static string Of(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Qualify(md.GetString(type.Namespace), md.GetString(type.Name))
            : $"{Of(md, declaring)}/{md.GetString(type.Name)}";
    }

    public static string Of(MetadataReader md, ExportedTypeHandle handle)
    {
        var type = md.GetExportedType(handle);
        return type.Implementation.Kind == HandleKind.ExportedType
            ? $"{Of(md, (ExportedTypeHandle)type.Implementation)}/{md.GetString(type.Name)}"
            : Qualify(md.GetString(type.Namespace), md.GetString(type.Name));
    }

    private static string Qualify(string ns, string name) => ns.Length > 0 ? $"{ns}.{name}" : name;

    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => $"{elementType}&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => $"method {signature.ReturnType}({string.Join(", ", signature.ParameterTypes)})";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";
    public string GetPinnedType(string elementType) => $"{elementType} pinned";
    public string GetPointerType(string elementType) => $"{elementType}*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => $"{elementType}[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Of(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Of(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
