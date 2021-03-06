using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Sharp.Data;
using Sharp.Data.Databases;
using Sharp.Data.Log;
using Sharp.Migrations.Runners;

namespace Sharp.Migrations {
    public class Runner : IRunner {
        public static ISharpLogger Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType.Name);
        public static bool IgnoreDialectNotSupportedActions { get; set; }

		private Assembly _targetAssembly;
		private IDataClient _dataClient;
	    private DatabaseKind _databaseKind;
		private long _initialVersion;
	    private MigrationFactory _migrationFactory;

        public IVersionRepository VersionRepository { private get; set; }

	    public string MigrationGroup {
	        get { return VersionRepository.MigrationGroup; }
            set { VersionRepository.MigrationGroup = value; }
	    }

	    public long LastVersionNumber {
            get { return MigrationFinder.FindLastMigration(_targetAssembly); }
	    }

	    public long CurrentVersionNumber {
	        get {
	            if (_initialVersion == -1) {
                    GetCurrentVersion();
	            }
	            return _initialVersion;
	        }
	    }

        public Runner(IDataClient dataClient, Assembly targetAssembly, IVersionRepository versionRepository) {
            _dataClient = dataClient;
            _databaseKind = _dataClient.Database.Provider.DatabaseKind;
            _targetAssembly = targetAssembly ?? Assembly.GetCallingAssembly();
            VersionRepository = versionRepository;
            _migrationFactory = new MigrationFactory(_dataClient);
            _initialVersion = -1;
	    }
        
	    public Runner(IDataClient dataClient, Assembly targetAssembly) : this(dataClient, targetAssembly, new VersionRepository(dataClient)) {}

        public void Run(long targetVersion, string migrationGroup) {
            MigrationGroup = migrationGroup;
            Run(targetVersion);
        }

		public void Run(long targetVersion) {
		    List<MigrationInfo> migrationsFromAssembly = MigrationFinder.FindMigrations(_targetAssembly);
            VersionRepository.EnsureSchemaVersionTable(migrationsFromAssembly);
		    List<long> migrationsFromDatabase = VersionRepository.GetAppliedMigrations();
		    MigrationPlan migrationPlan = CreateMigrationPlan(migrationsFromDatabase, migrationsFromAssembly, targetVersion);
            RunMigrations(migrationPlan);
		}

        private MigrationPlan CreateMigrationPlan(List<long> migrationsFromDatabase, List<MigrationInfo> migrationsFromAssembly, long targetVersion) {
            long currentVersion = migrationsFromDatabase.Count == 0 ? 0 : migrationsFromDatabase.Last();
            if (targetVersion < 0) {
                targetVersion = migrationsFromAssembly.Last().Version;
            }
            if (targetVersion < currentVersion) { //down
                return new MigrationPlan(currentVersion, targetVersion, migrationsFromAssembly
                      .Where(m => m.Version > targetVersion && migrationsFromDatabase.Contains(m.Version))
                      .Reverse().ToList());
            }
            return new MigrationPlan(currentVersion, targetVersion, migrationsFromAssembly
                      .Where(m => !migrationsFromDatabase.Contains(m.Version) && m.Version <= targetVersion)
                      .ToList());
	    }

	    protected virtual void GetCurrentVersion() {
			_initialVersion = VersionRepository.GetCurrentVersion();
		}

	    private void RunMigrations(MigrationPlan migrationPlan) {
	        if (NoWorkToDo(migrationPlan)) {
	            Log.Info("No migrations to perform");
	            return;
	        }
	        Log.Info("Starting migrations");
	        Log.Info("Current version is " + migrationPlan.CurrentVersion);
	        Log.Info("Target version is " + migrationPlan.TargetVersion);
	        Log.Info(String.Format("Migrate action is: {0} from {1} to {2}",
	            (migrationPlan.IsUp ? "UP" : "DOWN"),
	            migrationPlan.CurrentVersion,
	            migrationPlan.TargetVersion));

	        foreach (var migrationInfo in migrationPlan.OrderedMigrationsToRun) {
	            try {
	                if (!migrationInfo.MigratesFor(_databaseKind)) {
	                    Log.Info(String.Format(" -> [{0}] {1} {2}() NOT PERFORMED for database {3}", migrationInfo.Version,
	                        migrationInfo.Name, migrationPlan.Direction, _databaseKind));
	                    UpdateCurrentVersion(migrationInfo, migrationPlan.Direction);
	                    return;
	                }
                    Log.Info(String.Format(" -> [{0}] {1} {2}()", migrationInfo.Version, migrationInfo.Name, migrationPlan.Direction));
	                var migration = _migrationFactory.CreateMigration(migrationInfo.MigrationType);
	                if (migrationPlan.IsUp) {
	                    migration.Up();
	                }
	                else {
	                    migration.Down();
	                }
	                UpdateCurrentVersion(migrationInfo, migrationPlan.Direction);
	            }
	            catch (NotSupportedByDialect nse) {
	                HandleNotSupportedByDialectException(migrationInfo, nse);
	            }
	            catch (Exception ex) {
	                string errorMsg = String.Format("Error running migration {0}: {1}", migrationInfo.Name, ex);
	                Log.Error(errorMsg);
	                _dataClient.RollBack();
	                throw new MigrationException(errorMsg, ex);
	            }
	        }
            Log.Info("Done. Current version: " + migrationPlan.TargetVersion);
	    }

	    private void UpdateCurrentVersion(MigrationInfo migrationInfo, Direction direction) {
	        if (direction == Direction.Up) {
	            VersionRepository.InsertVersion(migrationInfo);
	        }
	        else {
	            VersionRepository.RemoveVersion(migrationInfo);
	        }
		}

		private bool NoWorkToDo(MigrationPlan migrationPlan) {
			return migrationPlan.OrderedMigrationsToRun.Count == 0;
		}
        
		private void HandleNotSupportedByDialectException(MigrationInfo migrationInfo, NotSupportedByDialect nse) {
			if (IgnoreDialectNotSupportedActions) {
				Log.Warn(
					String.Format(
						"Migration[{0}] NotSupportedException not thrown due user config. Dialect: {1} Function: {2} Msg: {3}",
                        migrationInfo.Name, nse.DialectName, nse.FunctionName, nse.Message));
				return;
			}
			throw nse;
		}
	}
}