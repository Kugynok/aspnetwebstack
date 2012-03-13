﻿using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.Common;
using System.Web.Http.Hosting;
using System.Web.Http.Routing;
using System.Web.Http.WebHost.Properties;
using System.Web.Http.WebHost.Routing;
using System.Web.Routing;

namespace System.Web.Http.WebHost
{
    /// <summary>
    /// A <see cref="IHttpAsyncHandler"/> that passes ASP.NET requests into the <see cref="HttpServer"/>
    /// pipeline and write the result back.
    /// </summary>
    public class HttpControllerHandler : IHttpAsyncHandler
    {
        internal static readonly string HttpContextBaseKey = "MS_HttpContext";

        private static readonly Lazy<HttpMessageInvoker> _server =
            new Lazy<HttpMessageInvoker>(
                () =>
                {
                    HttpServer server = new HttpServer(GlobalConfiguration.Configuration, GlobalConfiguration.Dispatcher);
                    return new HttpMessageInvoker(server);
                });

        private IHttpRouteData _routeData;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpControllerHandler"/> class.
        /// </summary>
        /// <param name="routeData">The route data.</param>
        public HttpControllerHandler(RouteData routeData)
        {
            if (routeData == null)
            {
                throw Error.ArgumentNull("routeData");
            }

            _routeData = new HostedHttpRouteData(routeData);
        }

        /// <summary>
        /// Gets a value indicating whether another request can use the <see cref="T:System.Web.IHttpHandler"/> instance.
        /// </summary>
        /// <returns>true if the <see cref="T:System.Web.IHttpHandler"/> instance is reusable; otherwise, false.</returns>
        bool IHttpHandler.IsReusable
        {
            get { return IsReusable; }
        }

        /// <summary>
        /// Gets a value indicating whether another request can use the <see cref="T:System.Web.IHttpHandler"/> instance.
        /// </summary>
        /// <returns>true if the <see cref="T:System.Web.IHttpHandler"/> instance is reusable; otherwise, false.</returns>
        protected virtual bool IsReusable
        {
            get { return false; }
        }

        /// <summary>
        /// Processes the request.
        /// </summary>
        /// <param name="httpContext">The HTTP context base.</param>
        void IHttpHandler.ProcessRequest(HttpContext httpContext)
        {
            ProcessRequest(new HttpContextWrapper(httpContext));
        }

        /// <summary>
        /// Begins processing the request.
        /// </summary>
        /// <param name="httpContext">The HTTP context.</param>
        /// <param name="callback">The callback.</param>
        /// <param name="state">The state.</param>
        /// <returns>An <see cref="IAsyncResult"/> that contains information about the status of the process. </returns>
        IAsyncResult IHttpAsyncHandler.BeginProcessRequest(HttpContext httpContext, AsyncCallback callback, object state)
        {
            return BeginProcessRequest(new HttpContextWrapper(httpContext), callback, state);
        }

        /// <summary>
        /// Provides an asynchronous process End method when the process ends.
        /// </summary>
        /// <param name="result">An <see cref="T:System.IAsyncResult"/> that contains information about the status of the process.</param>
        void IHttpAsyncHandler.EndProcessRequest(IAsyncResult result)
        {
            EndProcessRequest(result);
        }

        /// <summary>
        /// Processes the request.
        /// </summary>
        /// <param name="httpContextBase">The HTTP context base.</param>
        protected virtual void ProcessRequest(HttpContextBase httpContextBase)
        {
            throw Error.NotSupported(SRResources.ProcessRequestNotSupported, typeof(HttpControllerHandler));
        }

        /// <summary>
        /// Begins the process request.
        /// </summary>
        /// <param name="httpContextBase">The HTTP context base.</param>
        /// <param name="callback">The callback.</param>
        /// <param name="state">The state.</param>
        /// <returns>An <see cref="IAsyncResult"/> that contains information about the status of the process. </returns>
        [SuppressMessage("Microsoft.WebAPI", "CR4001:DoNotCallProblematicMethodsOnTask", Justification = "This is commented in great details.")]
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Object gets passed to a task")]
        protected virtual IAsyncResult BeginProcessRequest(HttpContextBase httpContextBase, AsyncCallback callback, object state)
        {
            HttpRequestMessage request = ConvertRequest(httpContextBase);

            // Add route data
            request.Properties[HttpPropertyKeys.HttpRouteDataKey] = _routeData;

            Task responseBodyTask = _server.Value.SendAsync(request, CancellationToken.None)
                .Then(response => ConvertResponse(httpContextBase, response, request))
                .FastUnwrap();

            TaskWrapperAsyncResult result = new TaskWrapperAsyncResult(responseBodyTask, state);

            if (callback != null)
            {
                if (result.IsCompleted)
                {
                    // If the underlying task is already finished, from our caller's perspective this is just
                    // a synchronous completion. See also DevDiv #346170.
                    result.CompletedSynchronously = true;
                    callback(result);
                }
                else
                {
                    // If the underlying task isn't yet finished, from our caller's perspective this will be
                    // an asynchronous completion. We'll use ContinueWith instead of Finally for two reasons:
                    //
                    // - Finally propagates the antecedent Task's exception, which we don't need to do here.
                    //   Out caller will eventually call EndProcessRequest, which correctly observes the
                    //   antecedent Task's exception anyway if it faulted.
                    //
                    // - Finally invokes the callback on the captured SynchronizationContext, which is
                    //   unnecessary when using APM (Begin / End). APM assumes that the callback is invoked
                    //   on an arbitrary ThreadPool thread with no SynchronizationContext set up, so
                    //   ContinueWith gets us closer to the desired semantic.
                    //
                    // There is still a race here: the Task might complete after the IsCompleted check above,
                    // so the callback might be invoked on another thread concurrently with the original
                    // thread's call to BeginProcessRequest. But we shouldn't concern ourselves with that;
                    // the caller has to be prepared for that possibility and do the right thing. We also
                    // don't need to worry about the callback throwing since the caller should give us a
                    // callback which is well-behaved.
                    result.CompletedSynchronously = false;
                    responseBodyTask.ContinueWith(_ =>
                    {
                        callback(result);
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Provides an asynchronous process End method when the process ends.
        /// </summary>
        /// <param name="result">An <see cref="T:System.IAsyncResult"/> that contains information about the status of the process.</param>
        protected virtual void EndProcessRequest(IAsyncResult result)
        {
            TaskWrapperAsyncResult asyncResult = (TaskWrapperAsyncResult)result;
            Contract.Assert(asyncResult != null);
            Task task = asyncResult.Task;

            // Check task result and unwrap any exceptions
            if (task.IsCanceled)
            {
                throw Error.OperationCanceled();
            }
            else if (task.IsFaulted)
            {
                throw task.Exception.GetBaseException();
            }
        }

        private static void CopyHeaders(HttpHeaders from, HttpContextBase to)
        {
            Contract.Assert(from != null);
            Contract.Assert(to != null);

            foreach (var header in from)
            {
                string name = header.Key;
                foreach (var value in header.Value)
                {
                    to.Response.AppendHeader(name, value);
                }
            }
        }

        private static void AddHeaderToHttpRequestMessage(HttpRequestMessage httpRequestMessage, string headerName, string[] headerValues)
        {
            Contract.Assert(httpRequestMessage != null);
            Contract.Assert(headerName != null);
            Contract.Assert(headerValues != null);

            if (!httpRequestMessage.Headers.TryAddWithoutValidation(headerName, headerValues))
            {
                httpRequestMessage.Content.Headers.TryAddWithoutValidation(headerName, headerValues);
            }
        }

        /// <summary>
        /// Converts a <see cref="HttpResponseMessage"/> to an <see cref="HttpResponseBase"/> and disposes the 
        /// <see cref="HttpResponseMessage"/> and <see cref="HttpRequestMessage"/> upon completion.
        /// </summary>
        /// <param name="httpContextBase">The HTTP context base.</param>
        /// <param name="response">The response to convert.</param>
        /// <param name="request">The request (which will be disposed).</param>
        /// <returns>A <see cref="Task"/> representing the conversion of an <see cref="HttpResponseMessage"/> to an <see cref="HttpResponseBase"/>
        /// including writing out any entity body.</returns>
        internal static Task ConvertResponse(HttpContextBase httpContextBase, HttpResponseMessage response, HttpRequestMessage request)
        {
            Contract.Assert(httpContextBase != null);
            Contract.Assert(response != null);
            Contract.Assert(request != null);

            HttpResponseBase httpResponseBase = httpContextBase.Response;
            httpResponseBase.StatusCode = (int)response.StatusCode;
            httpResponseBase.StatusDescription = response.ReasonPhrase;
            httpResponseBase.TrySkipIisCustomErrors = true;
            CopyHeaders(response.Headers, httpContextBase);
            CacheControlHeaderValue cacheControl = response.Headers.CacheControl;

            // TODO 335085: Consider this when coming up with our caching story
            if (cacheControl == null)
            {
                // DevDiv2 #332323. ASP.NET by default always emits a cache-control: private header.
                // However, we don't want requests to be cached by default.
                // If nobody set an explicit CacheControl then explicitly set to no-cache to override the
                // default behavior. This will cause the following response headers to be emitted:
                //     Cache-Control: no-cache
                //     Pragma: no-cache
                //     Expires: -1
                httpContextBase.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            }

            Task responseTask = null;
            if (response.Content != null)
            {
                CopyHeaders(response.Content.Headers, httpContextBase);

                // Turn off ASP output caching
                httpResponseBase.BufferOutput = false;

                responseTask = response.Content.CopyToAsync(httpResponseBase.OutputStream);
            }
            else
            {
                responseTask = TaskHelpers.Completed();
            }

            return responseTask.Finally(
                () =>
                {
                    request.DisposeRequestResources();
                    request.Dispose();
                    response.Dispose();
                });
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Caller becomes owner")]
        private static HttpRequestMessage ConvertRequest(HttpContextBase httpContextBase)
        {
            Contract.Assert(httpContextBase != null);

            HttpRequestBase requestBase = httpContextBase.Request;
            HttpMethod method = HttpMethodHelper.GetHttpMethod(requestBase.HttpMethod);
            Uri uri = requestBase.Url;
            HttpRequestMessage request = new HttpRequestMessage(method, uri);

            // TODO: Should we use GetBufferlessInputStream? Yes, as we don't need any of the parsing from ASP
            request.Content = new StreamContent(requestBase.InputStream);
            foreach (string headerName in requestBase.Headers)
            {
                string[] values = requestBase.Headers.GetValues(headerName);
                AddHeaderToHttpRequestMessage(request, headerName, values);
            }

            // Carry over properties
            if (httpContextBase.User != null)
            {
                request.Properties.Add(HttpPropertyKeys.UserPrincipalKey, httpContextBase.User);
            }

            // Add context to enable route lookup later on
            request.Properties.Add(HttpContextBaseKey, httpContextBase);

            return request;
        }
    }
}