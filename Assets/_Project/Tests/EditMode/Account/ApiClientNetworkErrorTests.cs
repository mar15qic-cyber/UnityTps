using Game.Account;
using Game.UI;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class ApiClientNetworkErrorTests
    {
        [Test]
        public void ExplicitTimeoutWinsOverTransportError()
        {
            Assert.That(ApiTransportFailureClassifier.Classify("Request timeout"), Is.EqualTo(ApiClientErrorCodes.Timeout));
        }

        [Test]
        public void NativeTimeoutMessageIsClassifiedAsTimeout()
        {
            Assert.That(ApiTransportFailureClassifier.Classify("Request timeout"), Is.EqualTo(ApiClientErrorCodes.Timeout));
        }

        [Test]
        public void ConnectionRefusedIsNotReportedAsTimeout()
        {
            Assert.That(ApiTransportFailureClassifier.Classify("Connection refused"), Is.EqualTo(ApiClientErrorCodes.Connection));
        }

        [Test]
        public void DnsAndTlsFailuresHaveDistinctCodes()
        {
            Assert.That(ApiTransportFailureClassifier.Classify("Could not resolve host"), Is.EqualTo(ApiClientErrorCodes.Dns));
            Assert.That(ApiTransportFailureClassifier.Classify("TLS certificate validation failed"), Is.EqualTo(ApiClientErrorCodes.Tls));
        }

        [Test]
        public void UnknownTransportFailureUsesGenericNetworkCode()
        {
            Assert.That(ApiTransportFailureClassifier.Classify("unexpected socket failure"), Is.EqualTo(ApiClientErrorCodes.Network));
        }

        [Test]
        public void UiMessagesExplainDifferentClientFailures()
        {
            Assert.That(ApiErrorMessages.ToUserMessage(ApiResult<HealthDto>.Fail(0, ApiClientErrorCodes.Timeout, "ignored")), Is.EqualTo("后端响应超时，请确认 API 正在运行后重试"));
            Assert.That(ApiErrorMessages.ToUserMessage(ApiResult<HealthDto>.Fail(0, ApiClientErrorCodes.Connection, "ignored")), Is.EqualTo("无法连接后端服务，请确认 API 已启动"));
        }
    }
}

