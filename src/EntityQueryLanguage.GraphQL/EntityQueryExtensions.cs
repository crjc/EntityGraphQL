using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using EntityQueryLanguage.GraphQL.Parsing;
using System.Diagnostics;
using EntityQueryLanguage.Schema;
using EntityQueryLanguage.Compiler;

namespace EntityQueryLanguage.GraphQL
{
    public static class EntityQueryExtensions
    {
        /// <summary>
        /// Extension method to query an object purely based on the schema of that object.null Note it creates a new MappedSchemaProvider each time.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="dataQuery"></param>
        /// <returns></returns>
        public static IDictionary<string, object> QueryObject<TType>(this TType context, string dataQuery, bool includeDebugInfo = false)
        {
            return QueryObject(context, dataQuery, new MappedSchemaProvider<TType>(), null, null, includeDebugInfo);
        }
        /// Function that returns the DataContext for the queries. If null _serviceProvider is used
        public static IDictionary<string, object> QueryObject<TType>(this TType context, string dataQuery, ISchemaProvider schemaProvider, IRelationHandler relationHandler = null, IMethodProvider methodProvider = null, bool includeDebugInfo = false)
        {
            if (methodProvider == null)
                methodProvider = new DefaultMethodProvider();
            Stopwatch timer = null;
            if (includeDebugInfo)
            {
                timer = new Stopwatch();
                timer.Start();
            }

            var queryData = new ConcurrentDictionary<string, object>();
            var result = new Dictionary<string, object>();

            try
            {
                var objectGraph = new GraphQLCompiler(schemaProvider, methodProvider, relationHandler).Compile(dataQuery);
                // Parallel.ForEach(objectGraph.Fields, node =>
                foreach (var node in objectGraph.Fields)
                {
                    try
                    {
                        var data = node.Execute(context);
                        queryData[node.Name] = data;
                    }
                    catch (Exception ex)
                    {
                        queryData[node.Name] = new { eql_error = ex.Message };
                    }
                }
                // );
            }
            catch (Exception ex)
            {
                // error with the whole query
                result["error"] = ex.Message;
            }
            if (includeDebugInfo && timer != null)
            {
                timer.Stop();
                result["_debug"] = new { TotalMilliseconds = timer.ElapsedMilliseconds };
            }
            result["data"] = queryData;

            return result;
        }
    }
}