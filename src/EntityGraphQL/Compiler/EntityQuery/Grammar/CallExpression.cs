using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using EntityGraphQL.Schema;

namespace EntityGraphQL.Compiler.EntityQuery.Grammar;

internal sealed class CallExpression(string name, List<IExpression>? arguments) : IExpression
{
    public Type Type => throw new NotImplementedException();

    public string Name { get; } = name;
    public List<IExpression>? Arguments { get; } = arguments;

    public Expression Compile(Expression? context, ISchemaProvider? schema, QueryRequestContext requestContext, IMethodProvider methodProvider)
    {
        throw new NotImplementedException();
    }
}
