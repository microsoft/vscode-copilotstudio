namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Abstractions;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Moq;
    using Moq.Protected;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

    public class DataverseClientTests
    {
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AccessToken = "access-token";
        private const string DisplayName = "NewAgent";
        private const string IconBase64 = "iconBased64";
        private const string SchemaName = "SchemaName";
        private const string UserAgent = "MCSVSCode-1.0.0";
        private readonly Guid _agentId = Guid.NewGuid();

        [Theory]
        [InlineData(SchemaName, SchemaName)]
        [InlineData("", "new_bot_123")]
        public async Task CreateNewAgentAsyncWithValidResponse(string inputSchemaName, string expectedSchemaName)
        {
            var client = CreateClientWithHandler((req, index) =>
            {
                var responseBody = JsonSerializer.Serialize(new
                {
                    botid = _agentId,
                    name = DisplayName,
                    iconbase64 = IconBase64,
                    schemaname = expectedSchemaName
                });
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                });
            });

            var result = await client.CreateNewAgentAsync(DisplayName, inputSchemaName, CancellationToken.None);

            Assert.Equal(_agentId, result.AgentId);
            Assert.Equal(DisplayName, result.DisplayName);
            Assert.Equal(IconBase64, result.IconBase64);
            Assert.Equal(expectedSchemaName, result.SchemaName);
        }

        [Fact]
        public async Task CreateNewAgentAsyncWithFailedResponse()
        {
            var client = CreateClientWithHandler((req, index) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateNewAgentAsync(DisplayName, SchemaName, CancellationToken.None));
            Assert.Contains("400", ex.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task GetAgentIdBySchemaNameAsyncWithAgent(bool exists)
        {
            var agentValue = exists ? new[] { new { botid = _agentId } } : Array.Empty<object>();
            var client = CreateClientWithHandler((req, index) =>
            {
                var responseBody = JsonSerializer.Serialize(new { value = agentValue });
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                });
            });

            var agentId = await client.GetAgentIdBySchemaNameAsync(SchemaName, CancellationToken.None);

            Assert.Equal(exists ? _agentId : Guid.Empty, agentId);
        }

        [Fact]
        public async Task GetAgentIdBySchemaNameAsyncWithFailure()
        {
            var client = CreateClientWithHandler((req, index) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAgentIdBySchemaNameAsync(SchemaName, CancellationToken.None));
        }

        [Fact]
        public async Task DownloadAllWorkflowsForAgentForValidWorkflow()
        {
            var workflowId = Guid.NewGuid();
            var botComponentId = Guid.NewGuid();
            int callIndex = 0;

            var client = CreateClientWithHandler((req, index) =>
            {
                callIndex++;
                string content = callIndex switch
                {
                    1 => JsonSerializer.Serialize(new { value = new[] { new { botcomponentid = botComponentId } } }),
                    2 => JsonSerializer.Serialize(new { value = new[] { new { workflowid = workflowId, botcomponentid = botComponentId } } }),
                    3 => JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                    new
                    {
                        workflowid = workflowId,
                        name = "TestWorkflow",
                        clientdata = "clientdata"
                    }
                }
                    }),
                    _ => JsonSerializer.Serialize(new { value = Array.Empty<object>() })
                };
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            });

            var workflows = await client.DownloadAllWorkflowsForAgentAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.Single(workflows);
            Assert.Equal("TestWorkflow", workflows[0].Name);
            Assert.Equal(workflowId, workflows[0].WorkflowId);
            Assert.Equal("clientdata", workflows[0].ClientData);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task DownloadAllWorkflowsForAgentWithInvalidAgent(string agentIdStr)
        {
            var client = CreateClientFromHttpClient(new HttpClient());
            Guid? agentId = string.IsNullOrEmpty(agentIdStr) ? null : Guid.Parse(agentIdStr);
            var workflows = await client.DownloadAllWorkflowsForAgentAsync(agentId, CancellationToken.None);
            Assert.Empty(workflows);
        }

        [Fact]
        public async Task UpdateWorkflowAsynWithNullWorkflow()
        {
            var client = CreateClientFromHttpClient(new HttpClient());
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.UpdateWorkflowAsync(Guid.NewGuid(), null, CancellationToken.None));
        }

        [Fact]
        public async Task UpdateWorkflowAsyncWithEmptyAgent()
        {
            var client = CreateClientFromHttpClient(new HttpClient());
            var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid() };
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.UpdateWorkflowAsync(Guid.Empty, workflow, CancellationToken.None));
        }

        [Fact]
        public async Task UpdateWorkflowAsyncWithValidWorkflow()
        {
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            int callIndex = 0;
            var workflow = new WorkflowMetadata { WorkflowId = workflowId, Name = "WorkflowToUpdate", ClientData = "clientdata" };

            var client = CreateClientWithHandler((req, index) =>
            {
                callIndex++;
                return Task.FromResult(index == 1 ? new HttpResponseMessage(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.NoContent));
            });

            await client.UpdateWorkflowAsync(agentId, workflow, CancellationToken.None);
            Assert.Equal(2, callIndex);
        }

        [Fact]
        public async Task UpdateWorkflowAsyncForNotExistWorkflow()
        {
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var botComponentId = Guid.NewGuid();
            int callIndex = 0;
            var workflow = new WorkflowMetadata { WorkflowId = workflowId, Name = "WorkflowToInsert", ClientData = "clientdata" };

            var client = CreateClientWithHandler((req, index) =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => throw new HttpRequestException(),
                    2 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)),
                    3 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { botcomponentid = botComponentId } } })) }),
                    4 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
                    5 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                };
            });

            await client.UpdateWorkflowAsync(agentId, workflow, CancellationToken.None);
            Assert.Equal(3, callIndex);
        }

        [Fact]
        public async Task InsertWorkflowAsyncWithNullWorkflow()
        {
            var client = CreateClientFromHttpClient(new HttpClient());
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.InsertWorkflowAsync(Guid.NewGuid(), null, CancellationToken.None));
        }

        [Fact]
        public async Task InsertWorkflowAsyncWithEmptyAgent()
        {
            var client = CreateClientFromHttpClient(new HttpClient());
            var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid() };
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.InsertWorkflowAsync(Guid.Empty, workflow, CancellationToken.None));
        }

        [Fact]
        public async Task InsertWorkflowAsyncWithValidWorkflow()
        {
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var botComponentId = Guid.NewGuid();
            int callIndex = 0;

            var workflow = new WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "WorkflowToInsert",
                ClientData = "clientdata"
            };

            var client = CreateClientWithHandler((req, index) =>
            {
                callIndex++;
                return Task.FromResult(index switch
                {
                    1 => new HttpResponseMessage(HttpStatusCode.Created),
                    2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { botcomponentid = botComponentId } } })) },
                    3 => new HttpResponseMessage(HttpStatusCode.NoContent),
                    4 => new HttpResponseMessage(HttpStatusCode.NoContent),
                    _ => new HttpResponseMessage(HttpStatusCode.OK)
                });
            });

            await client.InsertWorkflowAsync(agentId, workflow, CancellationToken.None);
            Assert.Equal(2, callIndex);
        }

        private static SyncDataverseClient CreateClientFromHttpClient(HttpClient httpClient)
        {
            var accessorMock = new Mock<IDataverseHttpClientAccessor>();
            accessorMock.Setup(a => a.CreateClient()).Returns(httpClient);
            var client = new SyncDataverseClient(accessorMock.Object, UserAgent);
            client.SetDataverseUrl(DataverseUrl);
            return client;
        }

        private SyncDataverseClient CreateClientWithHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> sendAsyncFunc)
        {
            int callIndex = 0;
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.IsAny<HttpRequestMessage>(),
                   ItExpr.IsAny<CancellationToken>()
               )
               .Returns<HttpRequestMessage, CancellationToken>((request, token) =>
               {
                   callIndex++;
                   return sendAsyncFunc(request, callIndex);
               });

            var httpClient = new HttpClient(handlerMock.Object);
            return CreateClientFromHttpClient(httpClient);
        }

        [Fact]
        public async Task ConnectionReferenceExistsAsync_ReturnsTrue_WhenConnectionExists()
        {
            // Arrange
            var connectionRefName = "cre6c_test.shared_connector.12345";
            var connectionRefId = Guid.NewGuid();
            var responseBody = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        connectionreferenceid = connectionRefId,
                        connectionreferencelogicalname = connectionRefName
                    }
                }
            });

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.IsAny<HttpRequestMessage>(),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act
            var exists = await client.ConnectionReferenceExistsAsync(connectionRefName, CancellationToken.None);

            // Assert
            Assert.True(exists);
        }

        [Fact]
        public async Task ConnectionReferenceExistsAsync_ReturnsFalse_WhenConnectionDoesNotExist()
        {
            // Arrange
            var connectionRefName = "cre6c_test.shared_connector.12345";
            var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.IsAny<HttpRequestMessage>(),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act
            var exists = await client.ConnectionReferenceExistsAsync(connectionRefName, CancellationToken.None);

            // Assert
            Assert.False(exists);
        }

        [Fact]
        public async Task ConnectionReferenceExistsAsync_ThrowsException_OnFailure()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.IsAny<HttpRequestMessage>(),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.InternalServerError
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ConnectionReferenceExistsAsync("test", CancellationToken.None));
            Assert.Contains("Dataverse request failed", ex.Message);
        }

        [Fact]
        public async Task CreateConnectionReferenceAsync_Succeeds_WithValidInput()
        {
            // Arrange
            var connectionRefName = "cre6c_test.shared_msnweather.12345";
            var connectorId = "/providers/Microsoft.PowerApps/apis/shared_msnweather";

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.Is<HttpRequestMessage>(req =>
                       req.Method == HttpMethod.Post &&
                       req.RequestUri != null &&
                       req.RequestUri.ToString().Contains("/connectionreferences")),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.Created,
                   Content = new StringContent("{}", Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act
            await client.CreateConnectionReferenceAsync(connectionRefName, connectorId, CancellationToken.None);

            // Assert - no exception thrown
            handlerMock.Protected().Verify(
               "SendAsync",
               Times.Once(),
               ItExpr.IsAny<HttpRequestMessage>(),
               ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task CreateConnectionReferenceAsync_ThrowsException_OnFailure()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.IsAny<HttpRequestMessage>(),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.BadRequest,
                   Content = new StringContent("Invalid request", Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.CreateConnectionReferenceAsync("test", "connector", CancellationToken.None));
            Assert.Contains("Dataverse request failed", ex.Message);
        }

        [Fact]
        public async Task EnsureConnectionReferenceExistsAsync_DoesNotCreate_WhenExists()
        {
            // Arrange
            var connectionRefName = "cre6c_test.shared_connector.12345";
            var connectorId = "/providers/Microsoft.PowerApps/apis/shared_connector";
            var connectionRefId = Guid.NewGuid();
            var responseBody = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        connectionreferenceid = connectionRefId,
                        connectionreferencelogicalname = connectionRefName
                    }
                }
            });

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Get),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act
            await client.EnsureConnectionReferenceExistsAsync(connectionRefName, connectorId, CancellationToken.None);

            // Assert - only GET was called, not POST
            handlerMock.Protected().Verify(
               "SendAsync",
               Times.Once(),
               ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Get),
               ItExpr.IsAny<CancellationToken>());

            handlerMock.Protected().Verify(
               "SendAsync",
               Times.Never(),
               ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
               ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task EnsureConnectionReferenceExistsAsync_Creates_WhenDoesNotExist()
        {
            // Arrange
            var connectionRefName = "cre6c_test.shared_connector.12345";
            var connectorId = "/providers/Microsoft.PowerApps/apis/shared_connector";
            var emptyResponseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });

            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Get),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent(emptyResponseBody, Encoding.UTF8, "application/json")
               });

            handlerMock
               .Protected()
               .Setup<Task<HttpResponseMessage>>(
                   "SendAsync",
                   ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                   ItExpr.IsAny<CancellationToken>()
               )
               .ReturnsAsync(new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.Created,
                   Content = new StringContent("{}", Encoding.UTF8, "application/json")
               });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            // Act
            await client.EnsureConnectionReferenceExistsAsync(connectionRefName, connectorId, CancellationToken.None);

            // Assert - both GET and POST were called
            handlerMock.Protected().Verify(
               "SendAsync",
               Times.Once(),
               ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Get),
               ItExpr.IsAny<CancellationToken>());

            handlerMock.Protected().Verify(
               "SendAsync",
               Times.Once(),
               ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
               ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task UserAgentHeaderTest()
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            HttpRequestMessage? capturedRequest = null;

            var responseBody = JsonSerializer.Serialize(new
            {
                botid = Guid.NewGuid(),
                name = "TestAgent",
                iconbase64 = "iconBase64Value",
                schemaname = "TestSchema"
            });

            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                })
                .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request);

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            await client.CreateNewAgentAsync("TestAgent", "TestSchema", CancellationToken.None);

            Assert.NotNull(capturedRequest);
            Assert.True(capturedRequest!.Headers.UserAgent.Any(), "User-Agent header should be present");
            Assert.Contains(capturedRequest.Headers.UserAgent, h => h.Product?.Name == UserAgent);
        }

        [Fact]
        public void WorkflowMetadataDeserializePayloadWithNullBusinessProcessType()
        {
            var json = @"
            {
                ""@odata.etag"": ""W/\""2861891\"""",
                ""workflowid"": ""e1b7b4aa-0b8c-4c25-8c18-9d9a3dfb5c42"",
                ""name"": ""TestFlow"",
                ""type"": 1,
                ""scope"": 4,
                ""category"": 5,
                ""businessprocesstype"": null,
                ""istransacted"": true,
                ""ondemand"": false,
                ""modernflowtype"": 1
            }";

            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            WorkflowMetadata? workflow = null;
            var exception = Record.Exception(() =>
            {
                workflow = JsonSerializer.Deserialize<WorkflowMetadata>(
                    element.GetRawText(),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            });

            Assert.Null(exception);
            Assert.NotNull(workflow);

            Assert.Equal("TestFlow", workflow!.Name);
            Assert.Equal(1, workflow.Type);
            Assert.Null(workflow.BusinessProcessType);
            Assert.True(workflow.IsTransacted);
        }

        [Fact]
        public async Task GetConnectionReferencesByLogicalNamesAsyncWithEmptyInput()
        {
            var client = CreateClientWithHandler((req, index) => throw new Exception("HTTP should not be called"));
            var result = await client.GetConnectionReferencesByLogicalNamesAsync(Enumerable.Empty<string>(), CancellationToken.None);
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetConnectionReferencesByLogicalNamesAsyncTest()
        {
            HttpRequestMessage? capturedRequest = null;

            var responseBody = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        connectionreferenceid = Guid.NewGuid(),
                        connectionreferencelogicalname = "cr1",
                        connectorid = "connector"
                    }
                }
            });

            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                })
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req);

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            var result = await client.GetConnectionReferencesByLogicalNamesAsync(
                new[] { "cr1" },
                CancellationToken.None);

            Assert.Single(result);
            Assert.NotNull(capturedRequest);

            var uri = Uri.UnescapeDataString(capturedRequest!.RequestUri!.ToString());

            Assert.Contains("connectionreferences", uri);
            Assert.Contains("connectionreferencelogicalname eq 'cr1'", uri);
        }

        [Fact]
        public async Task GetConnectionReferencesByLogicalNamesAsyncTestWithNullResponse()
        {
            var responseBody = JsonSerializer.Serialize(new { value = (object?)null });

            var client = CreateClientWithHandler((req, index) =>
                Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                }));

            var result = await client.GetConnectionReferencesByLogicalNamesAsync(
                new[] { "cr1" },
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public async Task DownloadAllWorkflowsForAgentExcludesTestCaseComponentType()
        {
            HttpRequestMessage? capturedRequest = null;

            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json")
                })
                .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                {
                    capturedRequest = req;
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            await client.DownloadAllWorkflowsForAgentAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.NotNull(capturedRequest);

            var uri = Uri.UnescapeDataString(capturedRequest!.RequestUri!.ToString());

            Assert.Contains("componenttype ne 19", uri);
            Assert.True(capturedRequest.RequestUri.ToString().Length < 30000);
        }

        [Fact]
        public async Task DownloadAllWorkflowsForAgentLargeComponentList()
        {
            int callCount = 0;

            var manyComponents = Enumerable.Range(0, 120)
                .Select(_ => new { botcomponentid = Guid.NewGuid() })
                .ToArray();

            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
                {
                    callCount++;

                    var content = callCount switch
                    {
                        1 => JsonSerializer.Serialize(new { value = manyComponents }),
                        _ => JsonSerializer.Serialize(new { value = Array.Empty<object>() })
                    };

                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(content, Encoding.UTF8, "application/json")
                    });
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = CreateClientFromHttpClient(httpClient);

            await client.DownloadAllWorkflowsForAgentAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.True(callCount > 2);
        }

        [Fact]
        public async Task DownloadWorkflowsBatchesWorkflowRequests()
        {
            var workflowIds = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
            var botComponentIds = workflowIds.Select(_ => Guid.NewGuid()).ToList();

            int requestCount = 0;

            var client = CreateClientWithHandler((req, index) =>
            {
                requestCount++;

                string content;

                if (requestCount % 2 == 1)
                {
                    int batchIndex = (requestCount - 1) / 2;
                    var batch = workflowIds.Skip(batchIndex * 50).Take(50).ToList();
                    content = JsonSerializer.Serialize(new
                    {
                        value = batch.Select((wf, i) => new
                        {
                            workflowid = wf,
                            botcomponentid = botComponentIds[workflowIds.IndexOf(wf)]
                        })
                    });
                }
                else
                {
                    int batchIndex = (requestCount - 2) / 2;
                    var batch = workflowIds.Skip(batchIndex * 50).Take(50).ToList();
                    content = JsonSerializer.Serialize(new
                    {
                        value = batch.Select(wf => new
                        {
                            workflowid = wf,
                            botcomponentid = botComponentIds[workflowIds.IndexOf(wf)],
                            name = $"Workflow_{wf}",
                            clientdata = "{}"
                        })
                    });
                }

                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            });

            var workflows = await client.DownloadAllWorkflowsForAgentAsync(Guid.NewGuid(), CancellationToken.None);

            Assert.True(requestCount > 1);
        }
    }
}