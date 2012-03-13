﻿using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Assert = Microsoft.TestCommon.AssertEx;

namespace System.Web.Http.WebHost
{
    public class HttpControllerHandlerTest
    {
        [Fact]
        public void ConvertResponse_IfResponseHasNoCacheControlDefined_SetsNoCacheCacheabilityOnAspNetResponse()
        {
            // Arrange
            Mock<HttpContextBase> contextMock = new Mock<HttpContextBase>() { DefaultValue = DefaultValue.Mock };
            HttpResponseMessage response = new HttpResponseMessage();
            HttpRequestMessage request = new HttpRequestMessage();

            // Act
            HttpControllerHandler.ConvertResponse(contextMock.Object, response, request);

            // Assert
            contextMock.Verify(c => c.Response.Cache.SetCacheability(HttpCacheability.NoCache));
        }

        [Fact]
        public void ConvertResponse_IfResponseHasCacheControlDefined_DoesNotSetCacheCacheabilityOnAspNetResponse()
        {
            // Arrange
            Mock<HttpContextBase> contextMock = new Mock<HttpContextBase>() { DefaultValue = DefaultValue.Mock };
            HttpResponseMessage response = new HttpResponseMessage();
            HttpRequestMessage request = new HttpRequestMessage();
            response.Headers.CacheControl = new CacheControlHeaderValue { Public = true };

            // Act
            HttpControllerHandler.ConvertResponse(contextMock.Object, response, request);

            // Assert
            contextMock.Verify(c => c.Response.Cache.SetCacheability(HttpCacheability.NoCache), Times.Never());
        }

        [Fact]
        public Task ConvertResponse_DisposesRequestAndResponse()
        {
            // Arrange
            Mock<HttpContextBase> contextMock = new Mock<HttpContextBase>() { DefaultValue = DefaultValue.Mock };
            contextMock.SetupGet((hcb) => hcb.Response.OutputStream).Returns(Stream.Null);

            HttpRequestMessage request = new HttpRequestMessage();
            HttpResponseMessage response = new HttpResponseMessage();

            // Act
            return HttpControllerHandler.ConvertResponse(contextMock.Object, response, request).ContinueWith(
                _ =>
                {
                    // Assert
                    Assert.ThrowsObjectDisposed(() => request.Method = HttpMethod.Get, typeof(HttpRequestMessage).FullName);
                    Assert.ThrowsObjectDisposed(() => response.StatusCode = HttpStatusCode.OK, typeof(HttpResponseMessage).FullName);
                });
        }

        [Fact]
        public Task ConvertResponse_DisposesRequestAndResponseWithContent()
        {
            // Arrange
            Mock<HttpContextBase> contextMock = new Mock<HttpContextBase>() { DefaultValue = DefaultValue.Mock };
            contextMock.SetupGet((hcb) => hcb.Response.OutputStream).Returns(Stream.Null);

            HttpRequestMessage request = new HttpRequestMessage() { Content = new StringContent("request") };
            HttpResponseMessage response = new HttpResponseMessage() { Content = new StringContent("response") };

            // Act
            return HttpControllerHandler.ConvertResponse(contextMock.Object, response, request).ContinueWith(
                _ =>
                {
                    // Assert
                    Assert.ThrowsObjectDisposed(() => request.Method = HttpMethod.Get, typeof(HttpRequestMessage).FullName);
                    Assert.ThrowsObjectDisposed(() => response.StatusCode = HttpStatusCode.OK, typeof(HttpResponseMessage).FullName);
                });
        }
    }
}