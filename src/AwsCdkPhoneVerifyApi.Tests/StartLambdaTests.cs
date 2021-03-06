using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService.Model;
using AwsCdkPhoneVerifyApi.StartLambda;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;
using OtpNet;

namespace AwsCdkPhoneVerifyApi.Tests
{
    [TestFixture]
    public class StartLambdaTests : BaseLambdaTest
    {
        private Function function;

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            function = new Function(sns, repo);
        }

        [Test]
        public async Task PhoneIsRequired()
        {
            // Arrange
            var startRequest = new StartRequest { Phone = null };
            var request = CreateRequest(startRequest);

            // Act
            var response = await function.ExecuteAsync(request, new TestLambdaContext());

            // Assert
            Assert.AreEqual(400, response.StatusCode);
            var error = JsonConvert.DeserializeObject<ErrorResponse>(response.Body);
            Assert.AreEqual("Phone required", error.Error);
        }

        [Test]
        public async Task PhoneIsInvalid()
        {
            // Arrange
            var startRequest = new StartRequest { Phone = "abc1234" };
            var request = CreateRequest(startRequest);

            // Act
            var response = await function.ExecuteAsync(request, new TestLambdaContext());

            // Assert
            Assert.AreEqual(400, response.StatusCode);
            var error = JsonConvert.DeserializeObject<ErrorResponse>(response.Body);
            Assert.AreEqual("Phone invalid", error.Error);
        }

        [Test]
        public async Task RateLimit()
        {
            // Arrange
            var startRequest = new StartRequest { Phone = phone };
            var request = CreateRequest(startRequest);

            // Mock
            var verifications = Enumerable.Repeat(new Verification { Created = DateTime.UtcNow }, 20).ToList();
            repo.GetLatestVerificationsAsync(phone, 10).Returns(verifications);

            // Act
            var response = await function.ExecuteAsync(request, new TestLambdaContext());

            // Assert
            Assert.AreEqual(429, response.StatusCode);
            var error = JsonConvert.DeserializeObject<ErrorResponse>(response.Body);
            Assert.AreEqual("Rate limit", error.Error);

        }


        [Test]
        public async Task VerificationIsExpired()
        {
            // Arrange
            var startRequest = new StartRequest { Phone = phone };
            var request = CreateRequest(startRequest);

            // Mock
            repo.GetLatestVersionAsync(phone).Returns(1);

            // Mock
            var current = new Verification
            {
                Id = Guid.NewGuid(),
                Phone = phone,
                Version = 1,
                Verified = null,
                Attempts = 0,
                Created = DateTime.UtcNow.AddMinutes(-4),
                SecretKey = Encoding.UTF8.GetBytes("secret")
            };
            repo.GetVerificationAsync(phone, 1).Returns(current);

            // Mock
            var verifications = Enumerable.Empty<Verification>().ToList();
            repo.GetLatestVerificationsAsync(phone, 10).Returns(verifications);

            // Mock
            var nextVersion = new Verification
            {
                Id = Guid.NewGuid(),
                Phone = phone,
                Version = 2,
                Verified = null,
                Attempts = 0,
                Created = DateTime.UtcNow,
                SecretKey = Encoding.UTF8.GetBytes("secret")
            };
            repo.InsertNextVersionAsync(phone, 1).Returns(nextVersion);

            // Mock
            sns.PublishAsync(Arg.Any<PublishRequest>()).Returns(new PublishResponse { MessageId = "test1" });

            // Act
            var response = await function.ExecuteAsync(request, new TestLambdaContext());

            // Assert
            Assert.AreEqual(200, response.StatusCode);
            var startResponse = JsonConvert.DeserializeObject<StartResponse>(response.Body);
            Assert.AreEqual(nextVersion.Id, startResponse.Id);

            var hotp = new Hotp(current.SecretKey);
            var code = hotp.ComputeHOTP(nextVersion.Version);
            await sns.Received(1).PublishAsync(Arg.Is<PublishRequest>(x => x.PhoneNumber == phone && x.Message == $"Your code is: {code}"));

        }

        [Test]
        public async Task VerificationIsNotExpired()
        {
            // Arrange
            var startRequest = new StartRequest { Phone = phone };
            var request = CreateRequest(startRequest);

            // Mock
            repo.GetLatestVersionAsync(phone).Returns(1);

            // Mock
            var current = new Verification
            {
                Id = Guid.NewGuid(),
                Phone = phone,
                Version = 1,
                Verified = null,
                Attempts = 0,
                Created = DateTime.UtcNow,
                SecretKey = Encoding.UTF8.GetBytes("secret")
            };
            repo.GetVerificationAsync(phone, 1).Returns(current);

            // Mock
            var verifications = Enumerable.Empty<Verification>().ToList();
            repo.GetLatestVerificationsAsync(phone, 10).Returns(verifications);

            // Mock
            sns.PublishAsync(Arg.Any<PublishRequest>()).Returns(new PublishResponse { MessageId = "test1" });

            // Act
            var response = await function.ExecuteAsync(request, new TestLambdaContext());

            // Assert
            Assert.AreEqual(200, response.StatusCode);
            await repo.DidNotReceive().InsertNextVersionAsync(phone, 1);

            var startResponse = JsonConvert.DeserializeObject<StartResponse>(response.Body);
            Assert.AreEqual(current.Id, startResponse.Id);

            var hotp = new Hotp(current.SecretKey);
            var code = hotp.ComputeHOTP(current.Version);
            await sns.Received(1).PublishAsync(Arg.Is<PublishRequest>(x => x.PhoneNumber == phone && x.Message == $"Your code is: {code}"));
        }
    }
}