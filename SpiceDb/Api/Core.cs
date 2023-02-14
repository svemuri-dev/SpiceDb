﻿using Authzed.Api.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using Grpc.Net.Client;
using SpiceDb.Enum;
using SpiceDb.Models;
using System.Text.RegularExpressions;
using Relationship = Authzed.Api.V1.Relationship;

namespace SpiceDb.Api;

internal class Core
{
    private PermissionsService.PermissionsServiceClient? _acl;
    private SchemaService.SchemaServiceClient? _schema;
    private CallOptions _callOptions;
    private Metadata? _headers;

    private readonly string _serverAddress;
    private readonly string _preSharedKey;

    /// <summary>
    /// Example:
    /// serverAddress   "http://localhost:50051"
    /// preSharedKey    "spicedb_token"
    /// </summary>
    /// <param name="serverAddress"></param>
    /// <param name="preSharedKey"></param>
    public Core(string serverAddress, string preSharedKey)
    {
        _serverAddress = serverAddress;
        _preSharedKey = preSharedKey;
        Start();
    }
    private void Start()
    {
        var channel = CreateAuthenticatedChannelAsync(_serverAddress);

        //initializes new clients for interacting with Authzed.
        _acl = new PermissionsService.PermissionsServiceClient(channel.Result);
        _schema = new SchemaService.SchemaServiceClient(channel.Result);

        _headers = new()
        {
            { "Authorization", $"Bearer {_preSharedKey}" }
        };

        if (_serverAddress.StartsWith("http:"))
        {
            _callOptions = new CallOptions(_headers);
        }
        else if (_serverAddress.StartsWith("https:"))
        {
            _callOptions = new CallOptions();
        }
        else
        {
            throw new ArgumentException("Expecting http or https in the authzed endpoint.");
        }

    }

    public async Task<ExpandPermissionTreeResponse?> ExpandPermissionAsync(string resourceType,
            string resourceId,
            string permission,
            ZedToken? zedToken = null,
            CacheFreshness cacheFreshness = CacheFreshness.AnyFreshness)
    {
        var req = new ExpandPermissionTreeRequest
        {
            Consistency = new Consistency { MinimizeLatency = true, AtExactSnapshot = zedToken },
            Permission = permission,
            Resource = new ObjectReference { ObjectType = resourceType, ObjectId = resourceId }
        };

        if (cacheFreshness == CacheFreshness.AtLeastAsFreshAs)
        {
            req.Consistency.AtLeastAsFresh = zedToken;
        }
        else if (cacheFreshness == CacheFreshness.MustRefresh || zedToken == null)
        {
            req.Consistency.FullyConsistent = true;
        }

        return await _acl!.ExpandPermissionTreeAsync(req, _callOptions);
    }

    public async Task<PermissionResponse> CheckPermissionAsync(string resourceType,
        string resourceId,
        string permission,
        string subjectType,
        string subjectId,
        Dictionary<string, object>? context = null,
        ZedToken? zedToken = null,
        CacheFreshness cacheFreshness = CacheFreshness.AnyFreshness)
    {
        var req = new CheckPermissionRequest
        {
            Consistency = new Consistency { MinimizeLatency = true, AtExactSnapshot = zedToken },
            Permission = permission,
            Resource = new ObjectReference { ObjectType = resourceType, ObjectId = resourceId },
            Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId } },
            Context = context?.ToStruct()
        };

        if (cacheFreshness == CacheFreshness.AtLeastAsFreshAs)
        {
            req.Consistency.AtLeastAsFresh = zedToken;
        }
        else if (cacheFreshness == CacheFreshness.MustRefresh || zedToken == null)
        {
            req.Consistency.FullyConsistent = true;
        }

        var call = await _acl!.CheckPermissionAsync(req, _callOptions);

        return new PermissionResponse
        {
            HasPermission = call?.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission,
            ZedToken = call?.CheckedAt
        };
    }

    public async Task<List<string>> GetResourcePermissionsAsync(string resourceType,
                                                           string permission,
                                                           string subjectType,
                                                           string subjectId,
                                                           ZedToken? zedToken = null,
                                                           CacheFreshness cacheFreshness = CacheFreshness.AnyFreshness)
    {
        LookupResourcesRequest req = new LookupResourcesRequest
        {
            Consistency = new Consistency { MinimizeLatency = true, AtExactSnapshot = zedToken },
            Permission = permission,
            ResourceObjectType = resourceType,
            Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId } }
        };

        if (cacheFreshness == CacheFreshness.AtLeastAsFreshAs)
        {
            req.Consistency.AtLeastAsFresh = zedToken;
        }
        else if (cacheFreshness == CacheFreshness.MustRefresh || zedToken == null)
        {
            req.Consistency.FullyConsistent = true;
        }

        //Server streaming call, reads messages streamed from the service
        var call = _acl!.LookupResources(req, _callOptions);

        var list = new List<string>();

        //The IAsyncStreamReader<T>.ReadAllAsync() extension method reads all messages from the response stream
        await foreach (var resp in call.ResponseStream.ReadAllAsync())
        {
            list.Add(resp.ResourceObjectId);
        }
        return list;
    }

    public async Task<List<Relationship>> ReadRelationshipsAsync(string resourceType, string optionalResourceId = "",
     string optionalRelation = "", string optionalSubjectType = "", string optionalSubjectId = "", string optionalSubjectRelation = "",
     ZedToken? zedToken = null,
     CacheFreshness cacheFreshness = CacheFreshness.AnyFreshness)
    {
        ReadRelationshipsRequest req = new ReadRelationshipsRequest()
        {
            Consistency = new Consistency { MinimizeLatency = true, AtExactSnapshot = zedToken },
            RelationshipFilter = new RelationshipFilter
            {
                ResourceType = resourceType,
                OptionalRelation = optionalRelation,
                OptionalResourceId = optionalResourceId
            }
        };
        if (!String.IsNullOrEmpty(optionalSubjectType))
        {
            req.RelationshipFilter.OptionalSubjectFilter = new SubjectFilter() { SubjectType = optionalSubjectType, OptionalSubjectId = optionalSubjectId };
            if (!String.IsNullOrEmpty(optionalSubjectRelation))
            {
                req.RelationshipFilter.OptionalSubjectFilter.OptionalRelation = new SubjectFilter.Types.RelationFilter() { Relation = optionalSubjectRelation };
            }
        }
        if (cacheFreshness == CacheFreshness.AtLeastAsFreshAs)
        {
            req.Consistency.AtLeastAsFresh = zedToken;
        }
        else if (cacheFreshness == CacheFreshness.MustRefresh || zedToken == null)
        {
            req.Consistency.FullyConsistent = true;
        }
        var call = _acl!.ReadRelationships(req, _callOptions);
        List<Relationship> list = new List<Relationship>();

        await foreach (var resp in call.ResponseStream.ReadAllAsync())
        {
            list.Add(resp.Relationship);
        }

        return list;
    }

    public async Task<string> ReadSchemaAsync()
    {
        ReadSchemaRequest req = new ReadSchemaRequest();
        ReadSchemaResponse resp = await _schema!.ReadSchemaAsync(req, _callOptions);
        return resp.SchemaText;
    }

    public async Task<WriteSchemaResponse> WriteSchemaAsync(string schema)
    {
        var re = @"(@(?:""[^""]*"")+|""(?:[^""\n\\]+|\\.)*""|'(?:[^'\n\\]+|\\.)*')|//.*|/\*(?s:.*?)\*/";

        schema = Regex.Replace(schema, re, "$1");

        WriteSchemaRequest req = new WriteSchemaRequest
        {
            Schema = schema
        };
        return await _schema!.WriteSchemaAsync(req, _callOptions);
    }

    private async Task<ChannelBase> CreateAuthenticatedChannelAsync(string address)
    {
        var token = await GetTokenAsync();
        var credentials = CallCredentials.FromInterceptor((context, metadata) =>
        {
            if (!string.IsNullOrEmpty(token))
            {
                metadata.Add("Authorization", $"Bearer {token}");
            }
            return Task.CompletedTask;
        });

        // SslCredentials is used here because this channel is using TLS.
        // CallCredentials can't be used with ChannelCredentials.Insecure on non-TLS channels.
        ChannelBase channel;
        if (address.StartsWith("http:"))
        {
            Uri baseUri = new Uri(address);
            channel = new Grpc.Core.Channel(baseUri.Host, baseUri.Port, ChannelCredentials.Insecure);
        }
        else
        {
            channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                //HttpHandler = handler,
                Credentials = ChannelCredentials.Create(new SslCredentials(), credentials)
            });
        }
        return channel;
    }

    private Task<string> GetTokenAsync()
    {
        return Task.FromResult(_preSharedKey);
    }

    public static bool UpdateRelationships(ref RepeatedField<RelationshipUpdate> updateCollection, RelationshipUpdate updateItem, bool addOrDelete = true)
    {
        if (addOrDelete)
        {
            updateCollection.Add(updateItem);
            return true;
        }
        else
        {
            return updateCollection.Remove(updateItem);
        }
    }

    public static RelationshipUpdate GetRelationshipUpdate(string resourceType, string resourceId,
           string relation, string subjectType, string subjectId, string optionalSubjectRelation = "",
           RelationshipUpdate.Types.Operation operation = RelationshipUpdate.Types.Operation.Touch)
    {
        return new RelationshipUpdate
        {
            Operation = operation,
            Relationship = new Relationship()
            {
                Resource = new ObjectReference { ObjectType = resourceType, ObjectId = resourceId },
                Relation = relation,
                Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId }, OptionalRelation = optionalSubjectRelation },
            }
        };
    }


    public async Task<WriteRelationshipsResponse> WriteRelationshipsAsync(RepeatedField<RelationshipUpdate> updateCollection)
    {
        WriteRelationshipsRequest req = new WriteRelationshipsRequest() { Updates = { updateCollection } };
        return await _acl!.WriteRelationshipsAsync(req, _callOptions);
    }



    public async Task<ZedToken> UpdateRelationshipAsync(string resourceType, string resourceId, string relation,
           string subjectType, string subjectId, string optionalSubjectRelation = "",
          RelationshipUpdate.Types.Operation operation = RelationshipUpdate.Types.Operation.Touch)
    {

        return await UpdateRelationshipsAsync(resourceType, resourceId, new[] { relation }, subjectType, subjectId, optionalSubjectRelation, operation);
    }

    public async Task<ZedToken> UpdateRelationshipsAsync(string resourceType, string resourceId, IEnumerable<string> relations,
            string subjectType, string subjectId, string optionalSubjectRelation = "",
           RelationshipUpdate.Types.Operation operation = RelationshipUpdate.Types.Operation.Touch)
    {
        RepeatedField<RelationshipUpdate> updateCollection = new RepeatedField<RelationshipUpdate>();

        foreach (var relation in relations)
        {
            var updateItem = GetRelationshipUpdate(resourceType, resourceId, relation.ToLowerInvariant(), subjectType, subjectId, optionalSubjectRelation, operation);
            UpdateRelationships(ref updateCollection, updateItem);
        }

        WriteRelationshipsResponse resp = await WriteRelationshipsAsync(updateCollection);
        return resp.WrittenAt;
    }
}

