using Mono.Cecil;
using Mono.Cecil.Cil;

// Uses the member mapping and IL-copy rules already exercised by the RC48
// release graft. Existing shipping definitions are retained in place.
internal sealed class ShippingTypeImporter
{
    private readonly ModuleDefinition target;
    private readonly ModuleDefinition donor;
    private readonly Dictionary<string, TypeDefinition> types = new();
    private readonly Dictionary<string, MethodDefinition> methods = new();
    private readonly Dictionary<string, FieldDefinition> fields = new();

    internal ShippingTypeImporter(ModuleDefinition target, ModuleDefinition donor)
    {
        this.target = target;
        this.donor = donor;
    }

    internal void Copy(params string[] names)
    {
        foreach (string name in names)
            if (target.GetType(name) != null) throw new InvalidDataException("Checkpoint graft already exists: " + name);
        var sources = names.Select(name => donor.GetType(name) ?? throw new InvalidDataException("Missing donor type " + name))
            .SelectMany(All).ToArray();
        foreach (var source in sources)
        {
            if (source.HasGenericParameters) throw new InvalidDataException("Generic donor type is unsupported.");
            var copy = new TypeDefinition(source.Namespace, source.Name, source.Attributes, null);
            types.Add(source.FullName, copy);
            if (source.IsNested) types[source.DeclaringType.FullName].NestedTypes.Add(copy);
            else target.Types.Add(copy);
        }
        foreach (var source in sources)
        {
            var copy = types[source.FullName];
            copy.BaseType = Type(source.BaseType);
            foreach (var contract in source.Interfaces)
                copy.Interfaces.Add(new InterfaceImplementation(Type(contract.InterfaceType)));
            foreach (var field in source.Fields)
            {
                var added = new FieldDefinition(field.Name, field.Attributes, Type(field.FieldType));
                if (field.HasConstant) added.Constant = field.Constant;
                copy.Fields.Add(added); fields.Add(field.FullName, added);
            }
            foreach (var method in source.Methods)
            {
                if (method.HasGenericParameters) throw new InvalidDataException("Generic donor method is unsupported.");
                var added = new MethodDefinition(method.Name, method.Attributes, Type(method.ReturnType)) { ImplAttributes = method.ImplAttributes };
                foreach (var parameter in method.Parameters)
                    added.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Type(parameter.ParameterType)));
                copy.Methods.Add(added); methods.Add(method.FullName, added);
            }
        }
        foreach (var source in sources)
        {
            var copy = types[source.FullName];
            Attributes(source, copy);
            foreach (var field in source.Fields) Attributes(field, fields[field.FullName]);
            foreach (var method in source.Methods)
            {
                var added = methods[method.FullName];
                Body(method, added); Attributes(method, added);
                foreach (var implementation in method.Overrides) added.Overrides.Add(Method(implementation));
                for (int index = 0; index < method.Parameters.Count; index++)
                    Attributes(method.Parameters[index], added.Parameters[index]);
            }
            foreach (var property in source.Properties)
            {
                var added = new PropertyDefinition(property.Name, property.Attributes, Type(property.PropertyType))
                {
                    GetMethod = property.GetMethod == null ? null : methods[property.GetMethod.FullName],
                    SetMethod = property.SetMethod == null ? null : methods[property.SetMethod.FullName]
                };
                Attributes(property, added); copy.Properties.Add(added);
            }
        }
    }

    private bool Local(TypeReference type) => type.Scope?.Name == donor.Name || type.Scope?.Name == donor.Assembly.Name.Name;

    private TypeReference Type(TypeReference type)
    {
        if (type is GenericParameter) return type;
        if (type is GenericInstanceType generic)
        {
            var copy = new GenericInstanceType(Type(generic.ElementType));
            foreach (var argument in generic.GenericArguments) copy.GenericArguments.Add(Type(argument));
            return copy;
        }
        if (type is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
        if (type is ArrayType array) return new ArrayType(Type(array.ElementType), array.Rank);
        if (type is PointerType pointer) return new PointerType(Type(pointer.ElementType));
        if (types.TryGetValue(type.FullName, out var added)) return added;
        if (Local(type))
        {
            var existing = target.Types.SelectMany(All).SingleOrDefault(candidate => candidate.FullName == type.FullName);
            if (existing != null) return existing;
            throw new InvalidDataException("Unmapped donor type: " + type.FullName);
        }
        return target.ImportReference(type);
    }

    private MethodReference Method(MethodReference method)
    {
        if (method is GenericInstanceMethod generic)
        {
            var copy = new GenericInstanceMethod(Method(generic.ElementMethod));
            foreach (var argument in generic.GenericArguments) copy.GenericArguments.Add(Type(argument));
            return copy;
        }
        if (methods.TryGetValue(method.FullName, out var added)) return added;
        if (Local(method.DeclaringType))
        {
            var existing = target.Types.SelectMany(All).SelectMany(type => type.Methods).SingleOrDefault(candidate => candidate.FullName == method.FullName);
            if (existing != null) return existing;
        }
        var imported = new MethodReference(method.Name, Type(method.ReturnType), Type(method.DeclaringType))
        { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
        foreach (var parameter in method.GenericParameters) imported.GenericParameters.Add(new GenericParameter(parameter.Name, imported));
        foreach (var parameter in method.Parameters) imported.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
        return target.ImportReference(imported);
    }

    private FieldReference Field(FieldReference field)
    {
        if (fields.TryGetValue(field.FullName, out var added)) return added;
        if (Local(field.DeclaringType))
        {
            var existing = target.Types.SelectMany(All).SelectMany(type => type.Fields).SingleOrDefault(candidate => candidate.FullName == field.FullName);
            if (existing != null) return existing;
        }
        return target.ImportReference(new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType)));
    }

    private void Body(MethodDefinition source, MethodDefinition copy)
    {
        if (!source.HasBody) return;
        copy.Body = new MethodBody(copy) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        var locals = source.Body.Variables.ToDictionary(local => local, local => new VariableDefinition(Type(local.VariableType)));
        foreach (var local in source.Body.Variables) copy.Body.Variables.Add(locals[local]);
        var instructions = source.Body.Instructions.ToDictionary(instruction => instruction, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in source.Body.Instructions) copy.Body.Instructions.Add(instructions[instruction]);
        foreach (var instruction in source.Body.Instructions)
        {
            var added = instructions[instruction];
            added.OpCode = instruction.OpCode;
            added.Operand = instruction.Operand switch
            {
                Instruction branch => instructions[branch],
                Instruction[] branches => branches.Select(branch => instructions[branch]).ToArray(),
                VariableDefinition local => locals[local],
                ParameterDefinition parameter => copy.Parameters[parameter.Index],
                MethodReference method => Method(method),
                FieldReference field => Field(field),
                TypeReference type => Type(type),
                CallSite => throw new InvalidDataException("Indirect donor call is unsupported."),
                _ => instruction.Operand
            };
        }
        Instruction? At(Instruction? instruction) => instruction == null ? null : instructions[instruction];
        foreach (var handler in source.Body.ExceptionHandlers)
            copy.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = At(handler.TryStart), TryEnd = At(handler.TryEnd), HandlerStart = At(handler.HandlerStart),
                HandlerEnd = At(handler.HandlerEnd), FilterStart = At(handler.FilterStart),
                CatchType = handler.CatchType == null ? null : Type(handler.CatchType)
            });
    }

    private CustomAttributeArgument Argument(CustomAttributeArgument argument) => new(Type(argument.Type), argument.Value switch
    {
        CustomAttributeArgument nested => Argument(nested),
        CustomAttributeArgument[] values => values.Select(Argument).ToArray(),
        TypeReference type => Type(type),
        _ => argument.Value
    });

    private void Attributes(ICustomAttributeProvider source, ICustomAttributeProvider destination)
    {
        foreach (var attribute in source.CustomAttributes)
        {
            var copy = new CustomAttribute(Method(attribute.Constructor));
            foreach (var argument in attribute.ConstructorArguments) copy.ConstructorArguments.Add(Argument(argument));
            foreach (var field in attribute.Fields) copy.Fields.Add(new CustomAttributeNamedArgument(field.Name, Argument(field.Argument)));
            foreach (var property in attribute.Properties) copy.Properties.Add(new CustomAttributeNamedArgument(property.Name, Argument(property.Argument)));
            destination.CustomAttributes.Add(copy);
        }
    }

    internal static IEnumerable<TypeDefinition> All(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(All)) yield return nested;
    }
}
