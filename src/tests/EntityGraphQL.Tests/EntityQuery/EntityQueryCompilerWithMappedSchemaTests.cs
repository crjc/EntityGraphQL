using System;
using System.Linq;
using EntityGraphQL.Schema;
using EntityGraphQL.Tests;
using EntityGraphQL.Tests.ApiVersion1;
using Xunit;

namespace EntityGraphQL.Compiler.EntityQuery.Tests;

/// Tests that our compiler correctly compiles all the basic parts of our language against a given schema provider
public class EntityQueryCompilerWithMappedSchemaTests
{
    [Fact]
    public void TestConversionToGuid()
    {
        var exp = EntityQueryCompiler.Compile("people.where(guid == \"6492f5fe-0869-4279-88df-7f82f8e87a67\")", new TestObjectGraphSchema(), new ExecutionOptions());
        dynamic result = exp.Execute(GetDataContext())!;
        Assert.Equal(1, Enumerable.Count(result));
    }

    [Fact]
    public void CompilesIdentityCall()
    {
        var exp = EntityQueryCompiler.Compile("people", new TestObjectGraphSchema(), new ExecutionOptions());
        dynamic result = exp.Execute(GetDataContext())!;
        Assert.Equal(1, Enumerable.Count(result));
    }

    [Fact]
    public void CompilesIdentityCallFullPath()
    {
        var schema = new TestObjectGraphSchema();
        var exp = EntityQueryCompiler.Compile("privateProjects.where(id == 8).count()", schema, new ExecutionOptions());
        Assert.Equal(0, exp.Execute(GetDataContext()));
        var exp2 = EntityQueryCompiler.Compile("privateProjects.count()", schema, new ExecutionOptions());
        Assert.Equal(1, exp2.Execute(GetDataContext()));
    }

    [Fact]
    public void CompilesTypeBuiltFromObject()
    {
        // no brackets so it reads it as someRelation.relation.id = (99 ? 'wooh' : 66) and fails as 99 is not a bool
        var exp = EntityQueryCompiler.Compile("defaultLocation.id == 10", new TestObjectGraphSchema(), new ExecutionOptions());
        Assert.True((bool)exp.Execute(GetDataContext())!);
    }

    [Fact]
    public void CompilesIfThenElseInlineFalseBrackets()
    {
        var exp = EntityQueryCompiler.Compile("(publicProjects.Count(id == 90) == 1) ? \"Yes\" : \"No\"", new TestObjectGraphSchema(), new ExecutionOptions());
        Assert.Equal("Yes", exp.Execute(GetDataContext()));
    }

    [Fact]
    public void CompilesIfThenElseTrue()
    {
        var exp = EntityQueryCompiler.Compile("if publicProjects.Count() > 1 then \"Yes\" else \"No\"", new TestObjectGraphSchema(), new ExecutionOptions());
        Assert.Equal("No", exp.Execute(GetDataContext()));
    }

    [Fact]
    public void CompilesAny()
    {
        var exp = EntityQueryCompiler.Compile("people.any(id > 90)", new TestObjectGraphSchema(), new ExecutionOptions());
        dynamic data = exp.Execute(GetDataContext())!;
        Assert.Equal(false, data);
    }

    private TestDataContext GetDataContext()
    {
        var db = new TestDataContext
        {
            Projects = [new Project { Id = 90, Type = 2 }, new Project { Id = 91, Type = 1 }],
            People = [new Person { Id = 4, Guid = new Guid("6492f5fe-0869-4279-88df-7f82f8e87a67") }],
            Locations = [new Location { Id = 10 }],
        };
        return db;
    }
}
