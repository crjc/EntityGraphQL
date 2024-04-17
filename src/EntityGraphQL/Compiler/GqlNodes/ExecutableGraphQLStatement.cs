using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Extensions;
using EntityGraphQL.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace EntityGraphQL.Compiler
{
    /// <summary>
    /// Base class for document statements that we "execute" - Query and Mutation. Execution runs the expression and gets the data result
    /// A fragment is just a definition
    /// </summary>
    public abstract class ExecutableGraphQLStatement : IGraphQLNode
    {
        public Expression? NextFieldContext { get; }
        public IGraphQLNode? ParentNode { get; }
        public ParameterExpression? RootParameter { get; }
        /// <summary>
        /// Variables that are expected to be passed in to execute this query
        /// </summary>
        protected Dictionary<string, ArgType> OpDefinedVariables { get; set; } = new();
        public ISchemaProvider Schema { get; protected set; }

        public ParameterExpression? OpVariableParameter { get; }

        public IField? Field { get; }
        public bool HasServices { get => Field?.Services.Count > 0; }

        public IReadOnlyDictionary<string, object> Arguments { get; }

        public string Name { get; }

        public List<BaseGraphQLField> QueryFields { get; } = new();
        protected List<GraphQLDirective> Directives { get; } = new();

        public ExecutableGraphQLStatement(ISchemaProvider schema, string name, Expression nodeExpression, ParameterExpression rootParameter, Dictionary<string, ArgType> opVariables)
        {
            Name = name;
            NextFieldContext = nodeExpression;
            RootParameter = rootParameter;
            OpDefinedVariables = opVariables;
            this.Schema = schema;
            Arguments = new Dictionary<string, object>();
            if (OpDefinedVariables.Count > 0)
            {
                var variableType = LinqRuntimeTypeBuilder.GetDynamicType(OpDefinedVariables.ToDictionary(f => f.Key, f => f.Value.RawType), "docVars");
                OpVariableParameter = Expression.Parameter(variableType, "docVars");
            }
        }

        public virtual async Task<ConcurrentDictionary<string, object?>> ExecuteAsync<TContext>(TContext? context, IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, Func<string, string> fieldNamer, ExecutionOptions options, QueryVariables? variables)
        {
            if (context == null && serviceProvider == null)
                throw new EntityGraphQLCompilerException("Either context or serviceProvider must be provided.");

            // build separate expression for all root level nodes in the op e.g. op is
            // query Op1 {
            //      people { name id }
            //      movies { released name }
            // }
            // people & movies will be the 2 fields that will be 2 separate expressions
            var result = new ConcurrentDictionary<string, object?>();

            object? docVariables = BuildDocumentVariables(ref variables);

            foreach (var fieldNode in QueryFields)
            {
                try
                {
#if DEBUG
                    Stopwatch? timer = null;
                    if (options.IncludeDebugInfo)
                    {
                        timer = new Stopwatch();
                        timer.Start();
                    }
#endif
                    var contextToUse = GetContextToUse(context, serviceProvider!, fieldNode)!;

                    (var data, var didExecute) = await CompileAndExecuteNodeAsync(new CompileContext(), contextToUse, serviceProvider, fragments, fieldNode, options, docVariables);
#if DEBUG
                    if (options.IncludeDebugInfo)
                    {
                        timer?.Stop();
                        result[$"__{fieldNode.Name}_timeMs"] = timer?.ElapsedMilliseconds;
                    }
#endif

                    if (didExecute)
                        result[fieldNode.Name] = data;
                }
                catch (EntityGraphQLValidationException)
                {
                    throw;
                }
                catch (EntityGraphQLFieldException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new EntityGraphQLFieldException(fieldNode.Name, ex);
                }
            }
            return result;
        }

        protected static TContext GetContextToUse<TContext>(TContext? context, IServiceProvider serviceProvider, BaseGraphQLField fieldNode)
        {
            if (context == null)
                return serviceProvider.GetService<TContext>()! ?? throw new EntityGraphQLCompilerException($"Could not find service of type {typeof(TContext).Name} to execute field {fieldNode.Name}");

            return context;
        }

        protected object? BuildDocumentVariables(ref QueryVariables? variables)
        {
            // inject document level variables - letting the query be cached and passing in different variables
            object? variablesToUse = null;

            if (OpDefinedVariables.Count > 0 && OpVariableParameter != null)
            {
                variables ??= new QueryVariables();
                variablesToUse = Activator.CreateInstance(OpVariableParameter.Type);
                foreach (var (name, argType) in OpDefinedVariables)
                {
                    try
                    {
                        var argValue = ExpressionUtil.ChangeType(variables.GetValueOrDefault(name) ?? argType.DefaultValue, argType.RawType, Schema);
                        if (argValue == null && argType.IsRequired)
                            throw new EntityGraphQLCompilerException($"Supplied variable '{name}' is null while the variable definition is non-null. Please update query document or supply a non-null value.");
                        OpVariableParameter.Type.GetField(name)!.SetValue(variablesToUse, argValue);
                    }
                    catch (EntityGraphQLCompilerException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new EntityGraphQLCompilerException($"Supplied variable '{name}' can not be applied to defined variable type '{argType.Type}'", ex);
                    }
                }
            }

            return variablesToUse;
        }

        private static List<ParameterExpression> ExtractConstants(Expression? expression)
        {
            var res = new List<ParameterExpression>();

            if (expression == null)
                return res;

            if (expression is ParameterExpression pe)
            {
                res.Add(pe);
                return res;
            }

            if (expression is MethodCallExpression mce)
            {
                foreach (var a in mce.Arguments) res.AddRange(ExtractConstants(a));
                res.AddRange(ExtractConstants(mce.Object));
            }
            else if (expression is ConditionalExpression ce)
            {
                res.AddRange(ExtractConstants(ce.Test));
            }
            else if (expression is BinaryExpression be)
            {
                res.AddRange(ExtractConstants(be.Left));
                res.AddRange(ExtractConstants(be.Right));
            }

            return res;
        }

        protected async Task<(object? result, bool didExecute)> CompileAndExecuteNodeAsync(CompileContext compileContext, object context, IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, BaseGraphQLField node, ExecutionOptions options, object? docVariables)
        {
            object? runningContext = context;

            var replacer = new ParameterReplacer();
            // For root/top level fields we need to first select the whole graph without fields that require services
            // so that EF Core 3.1+ can run and optimise the query against the DB
            // We then select the full graph from that context

            if (node.RootParameter == null)
                throw new EntityGraphQLCompilerException($"Root parameter not set for {node.Name}");

            Expression? expression = null;
            var contextParam = node.RootParameter;

            if (node.HasServicesAtOrBelow(fragments) && options.ExecuteServiceFieldsSeparately == true)
            {

                foreach (var arg in node.Arguments)
                {
                    var type = arg.Value.GetType();
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EntityGraphQL.Schema.EntityQueryType<>))
                    {
                        var v = (dynamic)arg.Value;
                        if (v?.Query?.Body != null)
                        {
                            var constants = (List<ParameterExpression>)ExtractConstants(v.Query.Body);
                            var d = constants.Distinct().ToList();
                            
                            compileContext.AddServices(d);
                        }
                    }
                } 

                // build this first as NodeExpression may modify ConstantParameters
                // this is without fields that require services
                expression = node.GetNodeExpression(compileContext, serviceProvider, fragments, OpVariableParameter, docVariables, contextParam, withoutServiceFields: true, null, null, isRoot: true, false, replacer);
                if (expression != null)
                {
                    // execute expression now and get a result that we will then perform a full select over
                    // This part is happening via EntityFramework if you use it
                    (runningContext, _) = await ExecuteExpressionAsync(expression, runningContext!, contextParam, serviceProvider, replacer, options, compileContext, node, false);
                    if (runningContext == null)
                        return (null, true);

                    // the full selection is now on the anonymous type returned by the selection without fields. We don't know the type until now
                    var newContextType = Expression.Parameter(runningContext.GetType(), "ctx_no_srv");

                    // core context data is fetched. Now fetch all the bulk resolvers
                    var bulkData = ResolveBulkLoaders(compileContext, serviceProvider, node, runningContext, replacer, newContextType);

                    // new context
                    compileContext = new(bulkData);

                    // we now know the selection type without services and need to build the full select on that type
                    // need to rebuild the full query
                    expression = node.GetNodeExpression(compileContext, serviceProvider, fragments, OpVariableParameter, docVariables, newContextType, false, replacementNextFieldContext: newContextType, null, isRoot: true, contextChanged: true, replacer);
                    contextParam = newContextType;
                }
            }

            if (expression == null)
            {
                // just do things normally
                expression = node.GetNodeExpression(compileContext, serviceProvider, fragments, OpVariableParameter, docVariables, contextParam, false, null, null, isRoot: true, contextChanged: false, replacer);
            }

            var data = await ExecuteExpressionAsync(expression, runningContext, contextParam, serviceProvider, replacer, options, compileContext, node, true);
            return data;
        }

        private static Dictionary<string, object> ResolveBulkLoaders(CompileContext compileContext, IServiceProvider? serviceProvider, BaseGraphQLField node, object? runningContext, ParameterReplacer replacer, ParameterExpression newContextType)
        {
            var bulkData = new Dictionary<string, object>();
            if (compileContext.BulkResolvers?.Count > 0)
            {
                foreach (var bulkResolver in compileContext.BulkResolvers)
                {
                    // rebuild list expression on new context
                    var toReplace = node.Field!.ResolveExpression!;
                    var listExpression = replacer.Replace(bulkResolver.ListExpression, toReplace, newContextType);
                    var newParam = Expression.Parameter(listExpression.Type.GetEnumerableOrArrayType()!, "bulkList");
                    // replace the data selection expression with the new context
                    var expReplacer = new ExpressionReplacer(bulkResolver.ExtractedFields, newParam, false, false, null);
                    var selection = expReplacer.Replace(bulkResolver.DataSelection.Body);
                    var selectionLambda = Expression.Lambda(selection, newParam);
                    selection = ExpressionUtil.MakeCallOnEnumerable(nameof(Enumerable.Select), [newParam.Type, selection.Type], listExpression, selectionLambda);

                    var bulkDataArgs = Expression.Lambda(selection, newContextType).Compile().DynamicInvoke([runningContext]);
                    var parameters = new List<ParameterExpression> { bulkResolver.FieldExpression.Parameters.First() };
                    var allArgs = new List<object?> { bulkDataArgs };
                    var bulkLoader = GraphQLHelper.InjectServices(serviceProvider!, compileContext.Services, allArgs, bulkResolver.FieldExpression.Body, parameters, replacer);
                    if (compileContext.ConstantParameters.Any())
                    {
                        parameters.AddRange(compileContext.ConstantParameters.Keys);
                        allArgs.AddRange(compileContext.ConstantParameters.Values);
                    }

                    var dataLoaded = Expression.Lambda(bulkLoader, parameters).Compile().DynamicInvoke([..allArgs])!;
                    bulkData[bulkResolver.Name] = dataLoaded;
                }
            }

            return bulkData;
        }

        private static async Task<(object? result, bool didExecute)> ExecuteExpressionAsync(Expression? expression, object context, ParameterExpression contextParam, IServiceProvider? serviceProvider, ParameterReplacer replacer, ExecutionOptions options, CompileContext compileContext, BaseGraphQLField node, bool isFinal)
        {
            // they had a query with a directive that was skipped, resulting in an empty query?
            if (expression == null)
                return (null, false);

            var allArgs = new List<object?> { context };

            var parameters = new List<ParameterExpression> { contextParam };

            // this is the full requested graph
            // inject dependencies into the fullSelection
            if (serviceProvider != null)
            {
                expression = GraphQLHelper.InjectServices(serviceProvider, compileContext.Services, allArgs, expression, parameters, replacer);
            }

            if (compileContext.ConstantParameters.Any())
            {
                parameters.AddRange(compileContext.ConstantParameters.Keys);
                allArgs.AddRange(compileContext.ConstantParameters.Values);
            }

            // evaluate everything using ToList(). But handle null result
            if (expression.Type.IsEnumerableOrArray() && !expression.Type.IsDictionary())
            {
                var returnType = typeof(List<>).MakeGenericType(expression.Type.GetEnumerableOrArrayType()!);
                expression = Expression.Call(typeof(EnumerableExtensions), nameof(EnumerableExtensions.ToListWithNullCheck), new[] { expression.Type.GetEnumerableOrArrayType()! }, expression, Expression.Constant(node.Field!.ReturnType.TypeNotNullable));
            }

            if (compileContext.BulkData != null)
            {
                parameters.Add(compileContext.BulkParameter!);
                allArgs.Add(compileContext.BulkData);
            }

            if (options.BeforeExecuting != null)
            {
                expression = options.BeforeExecuting.Invoke(expression, isFinal);
            }

            var lambdaExpression = Expression.Lambda(expression, parameters.ToArray());

#if DEBUG
            if (options.NoExecution)
                return (null, false);
#endif
            object? res = null;
            if (lambdaExpression.ReturnType.IsGenericType && lambdaExpression.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                res = await (dynamic?)lambdaExpression.Compile().DynamicInvoke(allArgs.ToArray());
            else
                res = lambdaExpression.Compile().DynamicInvoke(allArgs.ToArray());

            return (res, true);
        }

        public virtual void AddField(BaseGraphQLField field)
        {
            QueryFields.Add(field);
        }

        public void AddDirectives(IEnumerable<GraphQLDirective> graphQLDirectives)
        {
            Directives.AddRange(graphQLDirectives);
        }
    }
}