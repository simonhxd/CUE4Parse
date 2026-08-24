using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace CUE4Parse.UE4.Objects.UObject;

[SkipObjectRegistration]
public class UStruct : UField
{

    public FPackageIndex SuperStruct;
    public FPackageIndex[] Children;
    public FField[] ChildProperties;
    public KismetExpression[] ScriptBytecode;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);

        SuperStruct = Ar.Ver >= EUnrealEngineObjectUE3Version.MOVED_SUPERFIELD_TO_USTRUCT ? new FPackageIndex(Ar) : SuperField;

        if (Ar.Ver < EUnrealEngineObjectUE4Version.CONSOLIDATE_HEADER_PARSER_ONLY_PROPERTIES)
        {
            new FPackageIndex(Ar); // ScriptText
        }

        if (FFrameworkObjectVersion.Get(Ar) < FFrameworkObjectVersion.Type.RemoveUField_Next)
        {
            var firstChild = new FPackageIndex(Ar);
            Children = firstChild.IsNull ? [] : [firstChild];
        }
        else
        {
            Children = Ar.ReadArray(() => new FPackageIndex(Ar));
        }

        if (FCoreObjectVersion.Get(Ar) >= FCoreObjectVersion.Type.FProperties)
        {
            DeserializeProperties(Ar);
        }

        var bytecodeBufferSize = Ar.Read<int>();
        var serializedScriptSize = Ar.Read<int>();

        if (Ar.Owner!.Provider?.ReadScriptData == true && serializedScriptSize > 0)
        {
            var scriptData = Ar.ReadBytes(serializedScriptSize);
            var bFieldPathOwnerSerialization = FFieldPath.HasOwnerSerialization(Ar);
            if (!TryReadBytecode(Ar, scriptData, bytecodeBufferSize, bFieldPathOwnerSerialization, out ScriptBytecode, out var error) &&
                bFieldPathOwnerSerialization && FKismetPropertyPointer.UsesFieldPath(Ar))
            {
                /* Early 4.25 builds predate FFieldPathOwnerSerialization, so their property pointers carry no owner
                index. Unversioned packages don't say which layout they use, so fall back to the other one. */
                if (TryReadBytecode(Ar, scriptData, bytecodeBufferSize, false, out var retriedCode, out _))
                {
                    ScriptBytecode = retriedCode;
                    error = null;
                }
            }

            if (error != null) Log.Warning(error, "Failed to serialize script bytecode in {Name}", Name);
        }
        else
        {
            Ar.Position += serializedScriptSize;
        }
    }

    /**
     * Reads the whole script buffer. Returns whether it was read cleanly, meaning every serialized byte was consumed
     * and the expressions added up to the bytecode buffer size the engine wrote out.
     */
    private bool TryReadBytecode(FAssetArchive Ar, byte[] scriptData, int bytecodeBufferSize,
        bool bFieldPathOwnerSerialization, out KismetExpression[] bytecode, out Exception? error)
    {
        using var kismetAr = new FKismetArchive(Name, scriptData, Ar.Owner!, Ar.Versions)
        {
            bFieldPathOwnerSerialization = bFieldPathOwnerSerialization
        };

        var tempCode = new List<KismetExpression>();
        error = null;
        try
        {
            while (kismetAr.Position < kismetAr.Length)
            {
                tempCode.Add(kismetAr.ReadExpression());
            }
        }
        catch (Exception e)
        {
            error = e;
        }

        bytecode = [.. tempCode];
        return error == null && kismetAr.Position == kismetAr.Length && kismetAr.Index == bytecodeBufferSize;
    }

    private void DeserializeProperties(FAssetArchive Ar)
    {
        ChildProperties = Ar.ReadArray(() =>
        {
            var propertyTypeName = Ar.ReadFName();
            var prop = FField.Construct(propertyTypeName);
            prop.Deserialize(Ar);
            return prop;
        });
    }

    // ignore inner properties and return main one
    public bool GetProperty(FName name, out FField? property)
    {
        property = null;
        if (ChildProperties is null) return false;

        foreach (var item in ChildProperties)
        {
            if (item.Name.Text == name.Text)
            {
                property = item;
                return true;
            }
        }

        return false;
    }

    protected internal override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        if (SuperStruct is { IsNull: false } && (!SuperStruct.ResolvedObject?.Equals(Super) ?? false))
        {
            writer.WritePropertyName("SuperStruct");
            serializer.Serialize(writer, SuperStruct);
        }

        if (Children is { Length: > 0 })
        {
            writer.WritePropertyName("Children");
            serializer.Serialize(writer, Children);
        }

        if (ChildProperties is { Length: > 0 })
        {
            writer.WritePropertyName("ChildProperties");
            serializer.Serialize(writer, ChildProperties);
        }

        if (ScriptBytecode is { Length: > 0 })
        {
            writer.WritePropertyName("ScriptBytecode");
            writer.WriteStartArray();

            foreach (var expr in ScriptBytecode)
            {
                writer.WriteStartObject();
                expr.WriteJson(writer, serializer, true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }
}
