// AuthenticationTestHarnessLocal.cs
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Local;

namespace Silica.Authentication.Tests
{
    public static class AuthenticationTestHarnessLocal
    {
        public static async Task Run()
        {
            Console.WriteLine("[Test] Successful login");
            await TestSuccessfulLogin();

            Console.WriteLine("[Test] Invalid password");
            await TestInvalidPassword();

            Console.WriteLine("[Test] Unknown user");
            await TestUnknownUser();

            Console.WriteLine("[Test] Locked user");
            await TestLockedUser();

            Console.WriteLine("[Test] Password hasher round-trip");
            TestPasswordHasher();

            Console.WriteLine("[Test] InMemory store lookup");
            TestInMemoryStore();
        }

        private static PasswordHasher _hasher = new();
        // Align with production minimum policy enforced by LocalAuthenticator (310,000 iterations)
        private static LocalAuthenticationOptions _options = new() { HashIterations = 310000 };

        private static ILocalUserStore SeedStore(string username, string password, bool locked = false)
        {
            var salt = Encoding.UTF8.GetBytes(username + "#salt");
            // Clamp hashing iterations to the same minimum policy as verification
            int iterations = _options.HashIterations < 310000 ? 310000 : _options.HashIterations;
            var hash = _hasher.Hash(password, salt, iterations);

            var user = new LocalUser
            {
                Username = username,
                PasswordHash = hash,
                Salt = salt,
                Roles = new[] { "User" },
                IsLocked = locked
            };

            return new InMemoryLocalUserStore(new[] { user });
        }

        private static async Task TestSuccessfulLogin()
        {
            var store = SeedStore("alice", "password123");
            var auth = new LocalAuthenticator(store, _hasher, _options);

            var result = await auth.AuthenticateAsync(new AuthenticationContext
            {
                Username = "alice",
                Password = "password123"
            }, CancellationToken.None);

            if (!result.Succeeded || result.Principal != "alice")
                throw new InvalidOperationException("Successful login test failed.");
        }

        private static async Task TestInvalidPassword()
        {
            var store = SeedStore("bob", "correct");
            var auth = new LocalAuthenticator(store, _hasher, _options);

            var res = await auth.AuthenticateAsync(new AuthenticationContext { Username = "bob", Password = "wrong" }, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for invalid password.");
            if (res.FailureReason != "invalid_credentials")
                throw new InvalidOperationException("Unexpected failure reason for invalid password: " + (res.FailureReason ?? "<null>"));
        }

        private static async Task TestUnknownUser()
        {
            var store = SeedStore("carol", "secret");
            var auth = new LocalAuthenticator(store, _hasher, _options);

            var res = await auth.AuthenticateAsync(new AuthenticationContext { Username = "notfound", Password = "secret" }, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for unknown user.");
            if (res.FailureReason != "invalid_credentials")
                throw new InvalidOperationException("Unexpected failure reason for unknown user: " + (res.FailureReason ?? "<null>"));
        }

        private static async Task TestLockedUser()
        {
            var store = SeedStore("dave", "lockedpass", locked: true);
            var auth = new LocalAuthenticator(store, _hasher, _options);

            var res = await auth.AuthenticateAsync(new AuthenticationContext { Username = "dave", Password = "lockedpass" }, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for locked user.");
            if (res.FailureReason != "account_locked")
                throw new InvalidOperationException("Unexpected failure reason for locked user: " + (res.FailureReason ?? "<null>"));
        }

        private static void TestPasswordHasher()
        {
            var salt = Encoding.UTF8.GetBytes("user1");
            var hash = _hasher.Hash("mypassword", salt, _options.HashIterations);

            if (!_hasher.Verify("mypassword", hash, salt, _options.HashIterations))
                throw new InvalidOperationException("PasswordHasher verify failed.");
        }

        private static void TestInMemoryStore()
        {
            var user = new LocalUser { Username = "eve", PasswordHash = "hash" };
            var store = new InMemoryLocalUserStore(new[] { user });

            if (store.FindByUsername("eve") == null)
                throw new InvalidOperationException("InMemory store lookup failed.");
        }
    }
}
