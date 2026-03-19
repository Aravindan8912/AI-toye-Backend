using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JarvisBackend.Models;

/// <summary>Character role stored in MongoDB. Fetched by id (e.g. ironman) to build prompt.</summary>
public class Role
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = "";

    [BsonElement("role")]
    public string RoleKey { get; set; } = "";

    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("style")]
    public string Style { get; set; } = "";

    [BsonElement("maxLength")]
    public string MaxLength { get; set; } = "short";
}
