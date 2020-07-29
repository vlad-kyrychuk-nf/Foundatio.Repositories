﻿using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Foundatio.Repositories.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using System.Linq;
using System;
using Foundatio.Utility;

namespace Foundatio.Repositories.Elasticsearch.Tests {
    public sealed class MigrationTests : ElasticRepositoryTestBase {
        private MigrationIndex _migrationIndex;
        private MigrationManager _migrationManager;
        private MigrationStateRepository _migrationStateRepository;
        private ILockProvider _lockProvider;
        private IServiceProvider _serviceProvider;

        public MigrationTests(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<MigrationStateRepository>(LogLevel.Trace);
        }

        public override async Task InitializeAsync() {
            await base.InitializeAsync();
            _migrationIndex = new MigrationIndex(_configuration);
            await _migrationIndex.DeleteAsync();
            await _migrationIndex.ConfigureAsync();

            var cacheClient = new InMemoryCacheClient();
            var messageBus = new InMemoryMessageBus();
            _lockProvider = new CacheLockProvider(cacheClient, messageBus, Log);
            _migrationStateRepository = new MigrationStateRepository(_migrationIndex);

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<VersionedWithoutVersionMigration>();
            serviceCollection.AddSingleton<Version1Migration>();
            serviceCollection.AddSingleton<Version2Migration>();
            serviceCollection.AddSingleton<Version3Migration>();
            serviceCollection.AddSingleton<FailingMigration>();
            serviceCollection.AddSingleton<FailingResumableMigration>();
            serviceCollection.AddSingleton<RepeatableMigration>();
            _serviceProvider = serviceCollection.BuildServiceProvider();
            _migrationManager = new MigrationManager(_serviceProvider, _migrationStateRepository, _lockProvider, Log.CreateLogger<MigrationManager>());
        }

        [Fact]
        public async Task WillIgnoreVersionedMigrationWithoutVersion() {
            _migrationManager.AddMigration<VersionedWithoutVersionMigration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Empty(migrationStatus.PendingMigrations);
            Assert.False(migrationStatus.NeedsMigration);
        }

        [Fact]
        public async Task WillSetVersionToLatestIfNoMigrationsRun() {
            // no existing migrations, should add doc with latest version
            _migrationManager.AddMigration<Version3Migration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Empty(migrationStatus.PendingMigrations);
            Assert.False(migrationStatus.NeedsMigration);
            Assert.Equal(3, migrationStatus.CurrentVersion);

            await _client.Indices.RefreshAsync();

            var migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Single(migrations.Documents);
            var migrationState = migrations.Documents.First();
            Assert.Equal("3", migrationState.Id);
            Assert.Equal(MigrationType.Versioned, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.NotEqual(DateTime.MinValue, migrationState.CompletedUtc);
            Assert.Null(migrationState.ErrorMessage);
        }

        [Fact]
        public async Task CanRunPendingMigration() {
            await _migrationStateRepository.AddAsync(new MigrationState {
                Id = "1",
                Version = 1,
                MigrationType = MigrationType.Versioned,
                StartedUtc = SystemClock.UtcNow,
                CompletedUtc = SystemClock.UtcNow
            });

            _migrationManager.AddMigration<Version3Migration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Single(migrationStatus.PendingMigrations);
            Assert.True(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            var result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Success, result);

            await _client.Indices.RefreshAsync();

            var migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            var migrationState = migrations.Documents.First(d => d.Id == "3");
            Assert.Equal("3", migrationState.Id);
            Assert.Equal(MigrationType.Versioned, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.NotEqual(DateTime.MinValue, migrationState.CompletedUtc);
            Assert.Null(migrationState.ErrorMessage);
        }

        [Fact]
        public async Task CanRunRepeatableMigration() {
            await _migrationStateRepository.AddAsync(new MigrationState {
                Id = "1",
                Version = 1,
                MigrationType = MigrationType.Versioned,
                StartedUtc = SystemClock.UtcNow,
                CompletedUtc = SystemClock.UtcNow
            });

            _migrationManager.AddMigration<RepeatableMigration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Single(migrationStatus.PendingMigrations);
            Assert.True(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            var result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Success, result);

            await _client.Indices.RefreshAsync();

            var migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            var migrationState = migrations.Documents.FirstOrDefault(d => d.Id == typeof(RepeatableMigration).FullName);
            Assert.NotNull(migrationState);
            Assert.Equal(MigrationType.Repeatable, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.NotEqual(DateTime.MinValue, migrationState.CompletedUtc);
            Assert.Equal(0, migrationState.Version);
            Assert.Null(migrationState.ErrorMessage);

            migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Empty(migrationStatus.PendingMigrations);
            Assert.False(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Success, result);

            var repeatableMigration = _serviceProvider.GetRequiredService<RepeatableMigration>();
            repeatableMigration.SetVersion(1);

            migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Single(migrationStatus.PendingMigrations);
            Assert.True(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Success, result);

            migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            migrationState = migrations.Documents.FirstOrDefault(d => d.Id == typeof(RepeatableMigration).FullName);
            Assert.NotNull(migrationState);
            Assert.Equal(MigrationType.Repeatable, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.NotEqual(DateTime.MinValue, migrationState.CompletedUtc);
            Assert.Equal(1, migrationState.Version);
            Assert.Null(migrationState.ErrorMessage);
        }

        [Fact]
        public async Task CanHandleFailingMigration() {
            await _migrationStateRepository.AddAsync(new MigrationState {
                Id = "1",
                Version = 1,
                MigrationType = MigrationType.Versioned,
                StartedUtc = SystemClock.UtcNow,
                CompletedUtc = SystemClock.UtcNow
            });

            _migrationManager.AddMigration<FailingMigration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Single(migrationStatus.PendingMigrations);
            Assert.True(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            var result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Failed, result);

            var failingMigration = _serviceProvider.GetRequiredService<FailingMigration>();
            Assert.Equal(1, failingMigration.Attempts);

            await _client.Indices.RefreshAsync();

            var migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            var migrationState = migrations.Documents.FirstOrDefault(d => d.Id == "3");
            Assert.NotNull(migrationState);
            Assert.Equal(MigrationType.Versioned, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.Null(migrationState.CompletedUtc);
            Assert.Equal("Boom", migrationState.ErrorMessage);
        }

        [Fact]
        public async Task WillRetryFailingMigration() {
            await _migrationStateRepository.AddAsync(new MigrationState {
                Id = "1",
                Version = 1,
                MigrationType = MigrationType.Versioned,
                StartedUtc = SystemClock.UtcNow,
                CompletedUtc = SystemClock.UtcNow
            });

            _migrationManager.AddMigration<FailingResumableMigration>();

            var migrationStatus = await _migrationManager.GetMigrationStatus();
            Assert.Single(migrationStatus.PendingMigrations);
            Assert.True(migrationStatus.NeedsMigration);
            Assert.Equal(1, migrationStatus.CurrentVersion);

            var result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Failed, result);

            var failingMigration = _serviceProvider.GetRequiredService<FailingResumableMigration>();
            Assert.Equal(3, failingMigration.Attempts);

            await _client.Indices.RefreshAsync();

            var migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            var migrationState = migrations.Documents.FirstOrDefault(d => d.Id == "3");
            Assert.NotNull(migrationState);
            Assert.Equal(MigrationType.VersionedAndResumable, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.Null(migrationState.CompletedUtc);
            Assert.Equal("Boom", migrationState.ErrorMessage);

            // try again, should pass this time
            result = await _migrationManager.RunMigrationsAsync();
            Assert.Equal(MigrationResult.Success, result);

            migrations = await _migrationStateRepository.GetAllAsync();
            Assert.Equal(2, migrations.Documents.Count);
            migrationState = migrations.Documents.FirstOrDefault(d => d.Id == "3");
            Assert.NotNull(migrationState);
            Assert.Equal(MigrationType.VersionedAndResumable, migrationState.MigrationType);
            Assert.NotEqual(DateTime.MinValue, migrationState.StartedUtc);
            Assert.NotNull(migrationState.CompletedUtc);
            Assert.Null(migrationState.ErrorMessage);

        }
    }

    public class VersionedWithoutVersionMigration : MigrationBase {
        public override Task RunAsync() {
            throw new System.NotImplementedException();
        }
    }

    public class Version1Migration : MigrationBase {
        public override MigrationType MigrationType => MigrationType.Versioned;
        public override int? Version => 1;

        public override Task RunAsync() {
            return Task.Delay(100);
        }
    }

    public class Version2Migration : MigrationBase {
        public override MigrationType MigrationType => MigrationType.Versioned;
        public override int? Version => 2;

        public override Task RunAsync() {
            return Task.Delay(100);
        }
    }

    public class Version3Migration : MigrationBase {
        public override MigrationType MigrationType => MigrationType.Versioned;
        public override int? Version => 3;

        public override Task RunAsync() {
            return Task.Delay(100);
        }
    }

    public class FailingMigration : MigrationBase {
        public override MigrationType MigrationType => MigrationType.Versioned;
        public override int? Version => 3;
        public int Attempts { get; set; }

        public override async Task RunAsync() {
            Attempts++;
            throw new ApplicationException("Boom");
        }
    }

    public class FailingResumableMigration : MigrationBase {
        public override MigrationType MigrationType => MigrationType.VersionedAndResumable;
        public override int? Version => 3;
        public int Attempts { get; set; }

        public override async Task RunAsync() {
            Attempts++;
            if (Attempts <= 3)
                throw new ApplicationException("Boom");
        }
    }

    public class RepeatableMigration : MigrationBase {
        private int? _version = null;
        public void SetVersion(int? version) {
            _version = version;
        }

        public override int? Version => _version;
        public override MigrationType MigrationType => MigrationType.Repeatable;
        public int Runs { get; set; }

        public override Task RunAsync() {
            Runs++;
            return Task.CompletedTask;
        }
    }
}