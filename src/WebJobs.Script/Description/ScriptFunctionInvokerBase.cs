﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Bindings.Runtime;
using Microsoft.Azure.WebJobs.Script.Binding;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    [CLSCompliant(false)]
    public class ScriptFunctionInvokerBase : FunctionInvokerBase
    {
        public ScriptFunctionInvokerBase(ScriptHost host, FunctionMetadata functionMetadata) : base(host, functionMetadata)
        {
        }

        public override Task Invoke(object[] parameters)
        {
            throw new System.NotImplementedException();
        }

        protected virtual async Task ProcessInputBindingsAsync(object input, string functionInstanceOutputPath, Binder binder,
            Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings,
            Dictionary<string, object> bindingData, Dictionary<string, string> environmentVariables)
        {
            // if there are any input or output bindings declared, set up the temporary
            // output directory
            if (outputBindings.Count > 0 || inputBindings.Any())
            {
                Directory.CreateDirectory(functionInstanceOutputPath);
            }

            // process input bindings
            foreach (var inputBinding in inputBindings)
            {
                string filePath = Path.Combine(functionInstanceOutputPath, inputBinding.Metadata.Name);
                using (FileStream stream = File.OpenWrite(filePath))
                {
                    // If this is the trigger input, write it directly to the stream.
                    // The trigger binding is a special case because it is early bound
                    // rather than late bound as is the case with all the other input
                    // bindings.
                    if (inputBinding.Metadata.IsTrigger)
                    {
                        if (input is string)
                        {
                            using (StreamWriter sw = new StreamWriter(stream))
                            {
                                await sw.WriteAsync((string)input);
                            }
                        }
                        else if (input is byte[])
                        {
                            byte[] bytes = input as byte[];
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                        else if (input is Stream)
                        {
                            Stream inputStream = input as Stream;
                            await inputStream.CopyToAsync(stream);
                        }
                    }
                    else
                    {
                        // invoke the input binding
                        BindingContext bindingContext = new BindingContext
                        {
                            Binder = binder,
                            BindingData = bindingData,
                            DataType = DataType.Stream, 
                            Value = stream
                        };
                        await inputBinding.BindAsync(bindingContext);
                    }
                }

                environmentVariables[inputBinding.Metadata.Name] = Path.Combine(functionInstanceOutputPath,
                    inputBinding.Metadata.Name);
            }
        }

        protected virtual async Task ProcessOutputBindingsAsync(string functionInstanceOutputPath, Collection<FunctionBinding> outputBindings,
            object input, Binder binder, Dictionary<string, object> bindingData)
        {
            if (outputBindings == null)
            {
                return;
            }

            try
            {
                foreach (var outputBinding in outputBindings)
                {
                    string filePath = System.IO.Path.Combine(functionInstanceOutputPath, outputBinding.Metadata.Name);
                    if (File.Exists(filePath))
                    {
                        using (FileStream stream = File.OpenRead(filePath))
                        {
                            BindingContext bindingContext = new BindingContext
                            {
                                TriggerValue = input,
                                Binder = binder,
                                BindingData = bindingData,
                                Value = stream
                            };
                            await outputBinding.BindAsync(bindingContext);
                        }
                    }
                }
            }
            finally
            {
                // clean up the output directory
                if (outputBindings.Any() && Directory.Exists(functionInstanceOutputPath))
                {
                    Directory.Delete(functionInstanceOutputPath, recursive: true);
                }
            }
        }

        protected static object ConvertInput(object input)
        {
            if (input != null)
            {
                // perform any required input conversions
                HttpRequestMessage request = input as HttpRequestMessage;
                if (request != null)
                {
                    // TODO: Handle other content types? (E.g. byte[])
                    if (request.Content != null && request.Content.Headers.ContentLength > 0)
                    {
                        return ((HttpRequestMessage)input).Content.ReadAsStringAsync().Result;
                    }
                }
            }

            return input;
        }

        protected virtual void InitializeEnvironmentVariables(Dictionary<string, string> environmentVariables, string functionInstanceOutputPath, object input, Collection<FunctionBinding> outputBindings, ExecutionContext executionContext)
        {
            environmentVariables["InvocationId"] = executionContext.InvocationId.ToString();

            foreach (var outputBinding in outputBindings)
            {
                environmentVariables[outputBinding.Metadata.Name] = Path.Combine(functionInstanceOutputPath, outputBinding.Metadata.Name);
            }

            Type triggerParameterType = input.GetType();
            if (triggerParameterType == typeof(HttpRequestMessage))
            {
                HttpRequestMessage request = (HttpRequestMessage)input;
                environmentVariables["REQ_METHOD"] = request.Method.ToString();

                Dictionary<string, string> queryParams = request.GetQueryNameValuePairs().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var queryParam in queryParams)
                {
                    string varName = string.Format(CultureInfo.InvariantCulture, "REQ_QUERY_{0}", queryParam.Key.ToUpperInvariant());
                    environmentVariables[varName] = queryParam.Value;
                }

                foreach (var header in request.Headers)
                {
                    string varName = string.Format(CultureInfo.InvariantCulture, "REQ_HEADERS_{0}", header.Key.ToUpperInvariant());
                    environmentVariables[varName] = header.Value.First();
                }
            }
        }
    }
}
