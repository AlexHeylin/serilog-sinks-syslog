// Copyright 2018 Ionx Solutions (https://www.ionxsolutions.com)
// Ionx Solutions licenses this file to you under the Apache License,
// Version 2.0. You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy.Configuration;
using Xunit;
using Shouldly;
using static Serilog.Sinks.Syslog.Tests.Fixture;

namespace Serilog.Sinks.Syslog.Tests
{
    public class TcpSyslogSinkTests
    {
        private readonly List<string> messagesReceived = new List<string>();
        private X509Certificate2 clientCertificate;
        private X509Certificate2 serverCertificate;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly SyslogTcpConfig tcpConfig;
        private readonly BatchConfig batchConfig = new BatchConfig(3, BatchConfig.Default.Period, 10);
        private readonly IPEndPoint endpoint = GetFreeTcpEndPoint();
        private const SslProtocols SECURE_PROTOCOLS = SslProtocols.Tls11 | SslProtocols.Tls12;
        private readonly AsyncCountdownEvent countdown = new AsyncCountdownEvent(3);

        public TcpSyslogSinkTests()
        {
            this.tcpConfig = new SyslogTcpConfig
            {
                Host = "localhost",
                Port = this.endpoint.Port,
                KeepAlive = true,
                Formatter = new Rfc5424Formatter(Facility.Local0, "TestApp"),
                Framer = new MessageFramer(FramingType.OCTET_COUNTING)
            };
        }

        [Fact]
        public async Task Should_send_logs_to_tcp_syslog_service()
        {
            var sink = new SyslogTcpSink(this.tcpConfig, this.batchConfig);
            var log = GetLogger(sink);

            var receiver = new TcpSyslogReceiver(this.endpoint, null, SECURE_PROTOCOLS);

            receiver.MessageReceived += (_, msg) =>
            {
                this.messagesReceived.Add(msg);
                this.countdown.Signal();
            };

            var receiveTask = receiver.Start(this.cts.Token);

            log.Information("This is test message 1");
            log.Warning("This is test message 2");
            log.Error("This is test message 3");

            await this.countdown.WaitAsync(20000, this.cts.Token);

            // The server should have received all 3 messages sent by the sink
            this.messagesReceived.Count.ShouldBe(3);
            this.messagesReceived.ShouldContain(x => x.StartsWith("<134>"));
            this.messagesReceived.ShouldContain(x => x.StartsWith("<132>"));
            this.messagesReceived.ShouldContain(x => x.StartsWith("<131>"));

            sink.Dispose();
            this.cts.Cancel();
            await receiveTask;
        }

        [Fact]
        public async Task Should_send_logs_to_secure_tcp_syslog_service()
        {
            this.tcpConfig.KeepAlive = false; // Just to test the negative path
            this.tcpConfig.SecureProtocols = SECURE_PROTOCOLS;
            this.tcpConfig.CertProvider = new CertificateProvider(ClientCert);
            this.tcpConfig.CertValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                // So we know this callback was called
                this.serverCertificate = new X509Certificate2(certificate);
                return true;
            };

            var sink = new SyslogTcpSink(this.tcpConfig, this.batchConfig);
            var log = GetLogger(sink);

            var receiver = new TcpSyslogReceiver(this.endpoint, ServerCert, SECURE_PROTOCOLS);

            receiver.MessageReceived += (_, msg) =>
            {
                this.messagesReceived.Add(msg);
                this.countdown.Signal();
            };

            receiver.ClientAuthenticated += (_, cert) => this.clientCertificate = cert;

            var receiveTask = receiver.Start(this.cts.Token);

            log.Information("This is test message 1");
            log.Warning("This is test message 2");
            log.Error("This is test message 3");

            await this.countdown.WaitAsync(20000, this.cts.Token);

            // The server should have received all 3 messages sent by the sink
            this.messagesReceived.Count.ShouldBe(3);
            this.messagesReceived.ShouldContain(x => x.StartsWith("<134>"));
            this.messagesReceived.ShouldContain(x => x.StartsWith("<132>"));
            this.messagesReceived.ShouldContain(x => x.StartsWith("<131>"));

            // The sink should have presented the client certificate to the server
            this.clientCertificate.Thumbprint
                .ShouldBe(ClientCert.Thumbprint, StringCompareShould.IgnoreCase);

            // The sink should have seen the server's certificate in the validation callback
            this.serverCertificate.Thumbprint
                .ShouldBe(ServerCert.Thumbprint, StringCompareShould.IgnoreCase);

            sink.Dispose();
            this.cts.Cancel();
            await receiveTask;
        }

        // You can't set socket options *and* connect to an endpoint using a hostname - if
        // keep-alive is enabled, resolve the hostname to an IP
        // See https://github.com/dotnet/corefx/issues/26840
        [LinuxOnlyFact]
        public void Should_resolve_hostname_to_ip_on_linux_when_keepalive_enabled()
        {
            var sink = new SyslogTcpSink(this.tcpConfig, this.batchConfig);
            sink.Host.ShouldBe("127.0.0.1");
        }

        private static IPEndPoint GetFreeTcpEndPoint()
        {
            using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));

                return (IPEndPoint)sock.LocalEndPoint;
            }
        }
    }
}
