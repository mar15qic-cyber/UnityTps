using Game.Account;
using Game.UI;
using NUnit.Framework;
using UnityEngine.Networking;

namespace Game.Gameplay.Tests
{
    public sealed class ApiClientNetworkErrorTests
    {
        [Test]
        public void ExplicitTimeoutWinsOverTransportError()
        {
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.ConnectionError, "Cannot connect to destination host", true), Is.EqualTo(ApiClientErrorCodes.Timeout));
        }

        [Test]
        public void NativeTimeoutMessageIsClassifiedAsTimeout()
        {
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.ConnectionError, "Request timeout", false), Is.EqualTo(ApiClientErrorCodes.Timeout));
        }

        [Test]
        public void ConnectionRefusedIsNotReportedAsTimeout()
        {
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.ConnectionError, "Connection refused", false), Is.EqualTo(ApiClientErrorCodes.Connection));
        }

        [Test]
        public void DnsAndTlsFailuresHaveDistinctCodes()
        {
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.ConnectionError, "Could not resolve host", false), Is.EqualTo(ApiClientErrorCodes.Dns));
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.ConnectionError, "TLS certificate validation failed", false), Is.EqualTo(ApiClientErrorCodes.Tls));
        }

        [Test]
        public void DataProcessingFailureHasResponseCode()
        {
            Assert.That(ApiTransportFailureClassifier.Classify(UnityWebRequest.Result.DataProcessingError, "invalid body", false), Is.EqualTo(ApiClientErrorCodes.Response));
        }

        [Test]
        public void UiMessagesExplainDifferentClientFailures()
        {
            Assert.That(ApiErrorMessages.ToUserMessage(ApiResult<HealthDto>.Fail(0, ApiClientErrorCodes.Timeout, "ignored")), Is.EqualTo("后端响应超时，请确认 API 正在运行后重试"));
            Assert.That(ApiErrorMessages.ToUserMessage(ApiResult<HealthDto>.Fail(0, ApiClientErrorCodes.Connection, "ignored")), Is.EqualTo("无法连接后端服务，请确认 API 已启动"));
        }
    }
}

