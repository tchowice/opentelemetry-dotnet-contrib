// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#nullable disable

#if NETFRAMEWORK
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using OpenTelemetry.Instrumentation.Http.Implementation;
using OpenTelemetry.Tests;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Instrumentation.Http.Tests;

[Collection("Http")]
public class HttpWebRequestActivitySourceTests : IDisposable
{
    private static bool validateBaggage;
    private readonly IDisposable testServer;
    private readonly string testServerHost;
    private readonly int testServerPort;
    private readonly string netPeerName;
    private readonly int netPeerPort;

    static HttpWebRequestActivitySourceTests()
    {
        HttpClientTraceInstrumentationOptions options = new()
        {
            EnrichWithHttpWebRequest = (_, httpWebRequest) =>
            {
                VerifyHeaders(httpWebRequest);

                if (validateBaggage)
                {
                    ValidateBaggage(httpWebRequest);
                }
            },

            DisableUrlQueryRedaction = true,
        };

        HttpWebRequestActivitySource.TracingOptions = options;

        _ = Sdk.SuppressInstrumentation;
    }

    public HttpWebRequestActivitySourceTests()
    {
        Assert.Null(Activity.Current);
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = false;

        this.testServer = TestHttpServer.RunServer(
            ProcessServerRequest,
            out this.testServerHost,
            out this.testServerPort);

        this.netPeerName = this.testServerHost;
        this.netPeerPort = this.testServerPort;

        void ProcessServerRequest(HttpListenerContext context)
        {
            var redirects = context.Request.QueryString["redirects"];
            if (!string.IsNullOrWhiteSpace(redirects) && int.TryParse(redirects, out var parsedRedirects) && parsedRedirects > 0)
            {
                context.Response.Redirect(this.BuildRequestUrl(queryString: $"redirects={--parsedRedirects}"));
                context.Response.OutputStream.Close();
                return;
            }

            string responseContent;
            if (context.Request.QueryString["skipRequestContent"] == null)
            {
                using var readStream = new StreamReader(context.Request.InputStream);

                responseContent = readStream.ReadToEnd();
            }
            else
            {
                responseContent = $"{{\"Id\":\"{Guid.NewGuid()}\"}}";
            }

            var responseCode = context.Request.QueryString["responseCode"];
            context.Response.StatusCode = !string.IsNullOrWhiteSpace(responseCode) ? int.Parse(responseCode) : 200;

            if (context.Response.StatusCode != 204)
            {
                using var writeStream = new StreamWriter(context.Response.OutputStream);

                writeStream.Write(responseContent);
            }
            else
            {
                context.Response.OutputStream.Close();
            }
        }
    }

    public void Dispose()
    {
        this.testServer.Dispose();
    }

    /// <summary>
    /// A simple test to make sure the Http Diagnostic Source is added into the list of DiagnosticListeners.
    /// </summary>
    [Fact]
    public void TestHttpDiagnosticListenerIsRegistered()
    {
        var listenerFound = false;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = activitySource =>
            {
                if (activitySource.Name == HttpWebRequestActivitySource.ActivitySourceName)
                {
                    listenerFound = true;
                    return true;
                }

                return false;
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        Assert.True(listenerFound, "The Http Diagnostic Listener didn't get added to the AllListeners list.");
    }

    /// <summary>
    /// A simple test to make sure the Http Diagnostic Source is initialized properly after we subscribed to it, using
    /// the subscribe overload with just the observer argument.
    /// </summary>
    [Fact]
    public async Task TestReflectInitializationViaSubscription()
    {
        using var eventRecords = new ActivitySourceRecorder();

        // Send a random Http request to generate some events
        using (var client = new HttpClient())
        {
            (await client.GetAsync(new Uri(this.BuildRequestUrl()))).Dispose();
        }

        // Just make sure some events are written, to confirm we successfully subscribed to it.
        // We should have exactly one Start and one Stop event
        Assert.Equal(2, eventRecords.Records.Count);
    }

    /// <summary>
    /// Test to make sure we get both request and response events.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("POST", "skipRequestContent=1")]
    public async Task TestBasicReceiveAndResponseEvents(string method, string queryString = null)
    {
        var url = this.BuildRequestUrl(queryString: queryString);

        using var eventRecords = new ActivitySourceRecorder();

        // Send a random Http request to generate some events
        using (var client = new HttpClient())
        {
            (method == "GET"
                ? await client.GetAsync(new Uri(url))
                : await client.PostAsync(new Uri(url), new StringContent("hello world"))).Dispose();
        }

        // We should have exactly one Start and one Stop event
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        // Check to make sure: The first record must be a request, the next record must be a response.
        var activity = AssertFirstEventWasStart(eventRecords);

        VerifyActivityStartTags(this.netPeerName, this.netPeerPort, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var stopEvent));
        Assert.Equal("Stop", stopEvent.Key);

        VerifyActivityStopTags(200, activity);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestBasicReceiveAndResponseEventsWithoutSampling(string method)
    {
        using var eventRecords = new ActivitySourceRecorder(activitySamplingResult: ActivitySamplingResult.None);

        // Send a random Http request to generate some events
        using (var client = new HttpClient())
        {
            (method == "GET"
                ? await client.GetAsync(new Uri(this.BuildRequestUrl()))
                : await client.PostAsync(new Uri(this.BuildRequestUrl()), new StringContent("hello world"))).Dispose();
        }

        // There should be no events because we turned off sampling.
        Assert.Empty(eventRecords.Records);
    }

    [Theory]
    [InlineData("GET", 0)]
    [InlineData("GET", 1)]
    [InlineData("GET", 2)]
    [InlineData("GET", 3)]
    [InlineData("POST", 0)]
    [InlineData("POST", 1)]
    [InlineData("POST", 2)]
    [InlineData("POST", 3)]
    public async Task TestBasicReceiveAndResponseWebRequestEvents(string method, int mode)
    {
        var url = this.BuildRequestUrl();

        using var eventRecords = new ActivitySourceRecorder();

        // Send a random Http request to generate some events
        var webRequest = (HttpWebRequest)WebRequest.Create(new Uri(url));

        if (method == "POST")
        {
            webRequest.Method = method;

            Stream stream = null;
            switch (mode)
            {
                case 0:
                    stream = webRequest.GetRequestStream();
                    break;
                case 1:
                    stream = await webRequest.GetRequestStreamAsync();
                    break;
                case 2:
                    {
                        var state = new object();
                        using var handle = new EventWaitHandle(false, EventResetMode.ManualReset);
                        var asyncResult = webRequest.BeginGetRequestStream(
                            ar =>
                            {
                                Assert.Equal(state, ar.AsyncState);
                                handle.Set();
                            },
                            state);
                        stream = webRequest.EndGetRequestStream(asyncResult);
                        if (!handle.WaitOne(TimeSpan.FromSeconds(30)))
                        {
                            throw new InvalidOperationException();
                        }

                        handle.Dispose();
                    }

                    break;
                case 3:
                    {
                        using var handle = new EventWaitHandle(false, EventResetMode.ManualReset);
                        var state = new object();
                        webRequest.BeginGetRequestStream(
                            ar =>
                            {
                                stream = webRequest.EndGetRequestStream(ar);
                                Assert.Equal(state, ar.AsyncState);
                                handle.Set();
                            },
                            state);
                        if (!handle.WaitOne(TimeSpan.FromSeconds(30)))
                        {
                            throw new InvalidOperationException();
                        }

                        handle.Dispose();
                    }

                    break;
                default:
                    throw new NotSupportedException();
            }

            Assert.NotNull(stream);

            using var writer = new StreamWriter(stream);

            writer.WriteLine("hello world");
        }

        WebResponse webResponse = null;
        switch (mode)
        {
            case 0:
                webResponse = webRequest.GetResponse();
                break;
            case 1:
                webResponse = await webRequest.GetResponseAsync();
                break;
            case 2:
                {
                    var state = new object();
                    using var handle = new EventWaitHandle(false, EventResetMode.ManualReset);
                    var asyncResult = webRequest.BeginGetResponse(
                        ar =>
                        {
                            Assert.Equal(state, ar.AsyncState);
                            handle.Set();
                        },
                        state);
                    webResponse = webRequest.EndGetResponse(asyncResult);
                    if (!handle.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        throw new InvalidOperationException();
                    }

                    handle.Dispose();
                }

                break;
            case 3:
                {
                    using var handle = new EventWaitHandle(false, EventResetMode.ManualReset);
                    var state = new object();
                    webRequest.BeginGetResponse(
                        ar =>
                        {
                            webResponse = webRequest.EndGetResponse(ar);
                            Assert.Equal(state, ar.AsyncState);
                            handle.Set();
                        },
                        state);
                    if (!handle.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        throw new InvalidOperationException();
                    }

                    handle.Dispose();
                }

                break;
            default:
                throw new NotSupportedException();
        }

        Assert.NotNull(webResponse);

        using var reader = new StreamReader(webResponse.GetResponseStream());

        reader.ReadToEnd(); // Make sure response is not disposed.

        // We should have exactly one Start and one Stop event
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        // Check to make sure: The first record must be a request, the next record must be a response.
        var activity = AssertFirstEventWasStart(eventRecords);

        VerifyActivityStartTags(this.netPeerName, this.netPeerPort, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var stopEvent));
        Assert.Equal("Stop", stopEvent.Key);

        VerifyActivityStopTags(200, activity);
    }

    [Fact]
    public async Task TestTraceStateAndBaggage()
    {
        try
        {
            using var eventRecords = new ActivitySourceRecorder();

            using var parent = new Activity("w3c activity");
            parent.SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());
            parent.TraceStateString = "some=state";
            parent.Start();

            Baggage.SetBaggage("k", "v");

            // Send a random Http request to generate some events
            using (var client = new HttpClient())
            {
                (await client.GetAsync(new Uri(this.BuildRequestUrl()))).Dispose();
            }

            parent.Stop();

            Assert.Equal(2, eventRecords.Records.Count);

            // Check to make sure: The first record must be a request, the next record must be a response.
            _ = AssertFirstEventWasStart(eventRecords);
        }
        finally
        {
            this.CleanUpActivity();
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task DoNotInjectTraceParentWhenPresent(string method)
    {
        try
        {
            using var eventRecords = new ActivitySourceRecorder();

            // Send a random Http request to generate some events
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, this.BuildRequestUrl()))
            {
                request.Headers.Add("traceparent", "00-abcdef0123456789abcdef0123456789-abcdef0123456789-01");

                if (method == "GET")
                {
                    request.Method = HttpMethod.Get;
                }
                else
                {
                    request.Method = HttpMethod.Post;
                    request.Content = new StringContent("hello world");
                }

                (await client.SendAsync(request)).Dispose();
            }

            // No events are sent.
            Assert.Empty(eventRecords.Records);
        }
        finally
        {
            this.CleanUpActivity();
        }
    }

    /// <summary>
    /// Test to make sure we get both request and response events.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestResponseWithoutContentEvents(string method)
    {
        var url = this.BuildRequestUrl(queryString: "responseCode=204");

        using var eventRecords = new ActivitySourceRecorder();

        // Send a random Http request to generate some events
        using (var client = new HttpClient())
        {
            using var response = method == "GET"
                ? await client.GetAsync(new Uri(url))
                : await client.PostAsync(new Uri(url), new StringContent("hello world"));
        }

        // We should have exactly one Start and one Stop event
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        // Check to make sure: The first record must be a request, the next record must be a response.
        var activity = AssertFirstEventWasStart(eventRecords);

        VerifyActivityStartTags(this.netPeerName, this.netPeerPort, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var stopEvent));
        Assert.Equal("Stop", stopEvent.Key);

        VerifyActivityStopTags(204, activity);
    }

    /// <summary>
    /// Test that if request is redirected, it gets only one Start and one Stop event.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestRedirectedRequest(string method)
    {
        using var eventRecords = new ActivitySourceRecorder();

        using (var client = new HttpClient())
        {
            using var response = method == "GET"
                ? await client.GetAsync(new Uri(this.BuildRequestUrl(queryString: "redirects=10")))
                : await client.PostAsync(new Uri(this.BuildRequestUrl(queryString: "redirects=10")), new StringContent("hello world"));
        }

        // We should have exactly one Start and one Stop event
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));
    }

    /// <summary>
    /// Test exception in request processing: exception should have expected type/status and now be swallowed by reflection hook.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestRequestWithException(string method)
    {
        var host = Guid.NewGuid().ToString() + ".com";
        var url = method == "GET"
            ? $"http://{host}"
            : $"http://{host}";

        using var eventRecords = new ActivitySourceRecorder();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
        {
            return method == "GET"
                ? new HttpClient().GetAsync(new Uri(url))
                : new HttpClient().PostAsync(new Uri(url), new StringContent("hello world"));
        });

        // check that request failed because of the wrong domain name and not because of reflection
        var webException = (WebException)ex.InnerException;
        Assert.NotNull(webException);
        Assert.Equal(WebExceptionStatus.NameResolutionFailure, webException.Status);

        // We should have one Start event and one Stop event with an exception.
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        // Check to make sure: The first record must be a request, the next record must be an exception.
        var activity = AssertFirstEventWasStart(eventRecords);
        VerifyActivityStartTags(host, null, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var exceptionEvent));
        Assert.Equal("Stop", exceptionEvent.Key);

        Assert.NotEqual(ActivityStatusCode.Unset, activity.Status);
        Assert.Null(activity.StatusDescription);
    }

    /// <summary>
    /// Test request cancellation: reflection hook does not throw.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestCanceledRequest(string method)
    {
        var url = this.BuildRequestUrl();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var eventRecords = new ActivitySourceRecorder(_ => { cts.Cancel(); });

        using (var client = new HttpClient())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            {
                return method == "GET"
                    ? client.GetAsync(new Uri(url), cts.Token)
                    : client.PostAsync(new Uri(url), new StringContent("hello world"), cts.Token);
            });
            Assert.True(ex is TaskCanceledException or WebException);
        }

        // We should have one Start event and one Stop event with an exception.
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        var activity = AssertFirstEventWasStart(eventRecords);
        VerifyActivityStartTags(this.netPeerName, this.netPeerPort, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var exceptionEvent));
        Assert.Equal("Stop", exceptionEvent.Key);

        Assert.NotEqual(ActivityStatusCode.Unset, exceptionEvent.Value.Status);
        Assert.Null(exceptionEvent.Value.StatusDescription);
    }

    /// <summary>
    /// Test request connection exception: reflection hook does not throw.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestSecureTransportFailureRequest(string method)
    {
        var url = "https://expired.badssl.com/";

        using var eventRecords = new ActivitySourceRecorder();

        using (var client = new HttpClient())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            {
                // https://expired.badssl.com/ has an expired certificate.
                return method == "GET"
                    ? client.GetAsync(new Uri(url))
                    : client.PostAsync(new Uri(url), new StringContent("hello world"));
            });
            Assert.True(ex is HttpRequestException);
        }

        // We should have one Start event and one Stop event with an exception.
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        var activity = AssertFirstEventWasStart(eventRecords);
        VerifyActivityStartTags("expired.badssl.com", null, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var exceptionEvent));
        Assert.Equal("Stop", exceptionEvent.Key);

        Assert.NotEqual(ActivityStatusCode.Unset, exceptionEvent.Value.Status);
        Assert.Null(exceptionEvent.Value.StatusDescription);
    }

    /// <summary>
    /// Test request connection retry: reflection hook does not throw.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TestSecureTransportRetryFailureRequest(string method)
    {
        // This test sends an https request to an endpoint only set up for http.
        // It should retry. What we want to test for is 1 start, 1 exception event even
        // though multiple are actually sent.

        var url = this.BuildRequestUrl(useHttps: true);

        using var eventRecords = new ActivitySourceRecorder();

        using (var client = new HttpClient())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            {
                return method == "GET"
                    ? client.GetAsync(new Uri(url))
                    : client.PostAsync(new Uri(url), new StringContent("hello world"));
            });
            Assert.True(ex is HttpRequestException);
        }

        // We should have one Start event and one Stop event with an exception.
        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        var activity = AssertFirstEventWasStart(eventRecords);
        VerifyActivityStartTags(this.netPeerName, this.netPeerPort, method, url, activity);

        Assert.True(eventRecords.Records.TryDequeue(out var exceptionEvent));
        Assert.Equal("Stop", exceptionEvent.Key);

        Assert.NotEqual(ActivityStatusCode.Unset, exceptionEvent.Value.Status);
        Assert.Null(exceptionEvent.Value.StatusDescription);
    }

    [Fact]
    public async Task TestInvalidBaggage()
    {
        validateBaggage = true;
        Baggage
            .SetBaggage("key", "value")
            .SetBaggage("bad/key", "value")
            .SetBaggage("goodkey", "bad/value");

        using var eventRecords = new ActivitySourceRecorder();

        using (var client = new HttpClient())
        {
            (await client.GetAsync(new Uri(this.BuildRequestUrl()))).Dispose();
        }

        Assert.Equal(2, eventRecords.Records.Count);
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(1, eventRecords.Records.Count(rec => rec.Key == "Stop"));

        validateBaggage = false;
    }

    /// <summary>
    /// Test to make sure every event record has the right dynamic properties.
    /// </summary>
    [Fact]
    public async Task TestMultipleConcurrentRequests()
    {
        ServicePointManager.DefaultConnectionLimit = int.MaxValue;
        using var parentActivity = new Activity("parent").Start();
        using var eventRecords = new ActivitySourceRecorder();

        Dictionary<Uri, Tuple<WebRequest, WebResponse>> requestData = [];
        for (var i = 0; i < 10; i++)
        {
            var uriWithRedirect = new Uri(this.BuildRequestUrl(queryString: $"q={i}&redirects=3"));

            requestData[uriWithRedirect] = null;
        }

        // Issue all requests simultaneously
        using var httpClient = new HttpClient();
        Dictionary<Uri, Task<HttpResponseMessage>> tasks = [];

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var url in requestData.Keys)
        {
            tasks.Add(url, httpClient.GetAsync(url, cts.Token));
        }

        // wait up to 10 sec for all requests and suppress exceptions
        await Task.WhenAll(tasks.Select(t => t.Value).ToArray()).ContinueWith(async _ =>
        {
            foreach (var task in tasks)
            {
                (await task.Value)?.Dispose();
            }
        });

        // Examine the result. Make sure we got all successful requests.

        // Just make sure some events are written, to confirm we successfully subscribed to it. We should have
        // exactly 1 Start event per request and exactly 1 Stop event per response (if request succeeded)
        var successfulTasks = tasks.Where(t => t.Value.Status == TaskStatus.RanToCompletion);

        Assert.Equal(tasks.Count, eventRecords.Records.Count(rec => rec.Key == "Start"));
        Assert.Equal(successfulTasks.Count(), eventRecords.Records.Count(rec => rec.Key == "Stop"));

        // Check to make sure: We have a WebRequest and a WebResponse for each successful request
        foreach (var pair in eventRecords.Records)
        {
            var activity = pair.Value;

            Assert.True(
                pair.Key is "Start" or "Stop",
                "An unexpected event of name " + pair.Key + "was received");
        }
    }

    private static Activity AssertFirstEventWasStart(ActivitySourceRecorder eventRecords)
    {
        Assert.True(eventRecords.Records.TryDequeue(out var startEvent));
        Assert.Equal("Start", startEvent.Key);
        return startEvent.Value;
    }

    private static void VerifyHeaders(HttpWebRequest startRequest)
    {
        var tracestate = startRequest.Headers["tracestate"];
        Assert.Equal("some=state", tracestate);

        var baggage = startRequest.Headers["baggage"];
        Assert.Equal("k=v", baggage);

        var traceparent = startRequest.Headers["traceparent"];
        Assert.NotNull(traceparent);
        Assert.Matches("^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", traceparent);
    }

    private static void VerifyActivityStartTags(string netPeerName, int? netPeerPort, string method, string url, Activity activity)
    {
        Assert.NotNull(activity.TagObjects);
        Assert.Equal(method, activity.GetTagValue(SemanticConventions.AttributeHttpRequestMethod));
        if (netPeerPort != null)
        {
            Assert.Equal(netPeerPort, activity.GetTagValue(SemanticConventions.AttributeServerPort));
        }

        Assert.Equal(netPeerName, activity.GetTagValue(SemanticConventions.AttributeServerAddress));

        Assert.Equal(url, activity.GetTagValue(SemanticConventions.AttributeUrlFull));
    }

    private static void VerifyActivityStopTags(int statusCode, Activity activity)
    {
        Assert.Equal(statusCode, activity.GetTagValue(SemanticConventions.AttributeHttpResponseStatusCode));
    }

    private static void ValidateBaggage(HttpWebRequest request)
    {
        var baggage = request.Headers["baggage"].Split(',');

        Assert.Equal(3, baggage.Length);
        Assert.Contains("key=value", baggage);
        Assert.Contains("bad%2Fkey=value", baggage);
        Assert.Contains("goodkey=bad%2Fvalue", baggage);
    }

    private string BuildRequestUrl(bool useHttps = false, string path = "echo", string queryString = null)
    {
        return $"{(useHttps ? "https" : "http")}://{this.testServerHost}:{this.testServerPort}/{path}{(string.IsNullOrWhiteSpace(queryString) ? string.Empty : $"?{queryString}")}";
    }

    private void CleanUpActivity()
    {
        while (Activity.Current != null)
        {
            Activity.Current.Stop();
        }
    }

    /// <summary>
    /// <see cref="ActivitySourceRecorder"/> is a helper class for recording <see cref="HttpWebRequestActivitySource.ActivitySourceName"/> events.
    /// </summary>
    private class ActivitySourceRecorder : IDisposable
    {
        private readonly Action<KeyValuePair<string, Activity>> onEvent;
        private readonly ActivityListener activityListener;

        public ActivitySourceRecorder(Action<KeyValuePair<string, Activity>> onEvent = null, ActivitySamplingResult activitySamplingResult = ActivitySamplingResult.AllDataAndRecorded)
        {
            this.activityListener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == HttpWebRequestActivitySource.ActivitySourceName,
                ActivityStarted = this.ActivityStarted,
                ActivityStopped = this.ActivityStopped,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => activitySamplingResult,
            };

            ActivitySource.AddActivityListener(this.activityListener);

            this.onEvent = onEvent;
        }

        public ConcurrentQueue<KeyValuePair<string, Activity>> Records { get; } = new ConcurrentQueue<KeyValuePair<string, Activity>>();

        public void Dispose()
        {
            this.activityListener.Dispose();
        }

        public void ActivityStarted(Activity activity) => this.Record("Start", activity);

        public void ActivityStopped(Activity activity) => this.Record("Stop", activity);

        private void Record(string eventName, Activity activity)
        {
            var record = new KeyValuePair<string, Activity>(eventName, activity);

            this.Records.Enqueue(record);
            this.onEvent?.Invoke(record);
        }
    }
}
#endif
