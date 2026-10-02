using System.IO;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3ClientOperationIntentStoreTests
{
    [Fact]
    public void UnresolvedIntentSurvivesStoreRecreationAndRejectsChangedPayload()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-operation-intent-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation-intents.json");
            var operationKey = $"purchase-void:{Guid.CreateVersion7():D}";
            const string payload = "purchase|actor|original reason";
            var firstStore = new FileClientOperationIntentStore(path);
            var originalId = firstStore.GetOrCreate(operationKey, payload);

            var afterRestart = new FileClientOperationIntentStore(path);
            Assert.Equal(originalId, afterRestart.GetOrCreate(operationKey, payload));
            Assert.Throws<InvalidOperationException>(() => afterRestart.GetOrCreate(operationKey, "purchase|actor|changed reason"));
            Assert.DoesNotContain("original reason", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void CompletingIntentAllowsANewIdentityForTheSameBusinessOperationKey()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-operation-intent-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation-intents.json");
            const string operationKey = "thaka:payment:project-1";
            var store = new FileClientOperationIntentStore(path);
            var completedId = store.GetOrCreate(operationKey, "payment|100|cash");

            store.Complete(operationKey, completedId);

            var nextId = new FileClientOperationIntentStore(path).GetOrCreate(operationKey, "payment|125|cash");
            Assert.NotEqual(completedId, nextId);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void CorruptIntentFileFailsClosedInsteadOfMintingANewIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-operation-intent-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation-intents.json");
            File.WriteAllText(path, "{ not valid json");

            var exception = Assert.Throws<InvalidDataException>(() =>
                new FileClientOperationIntentStore(path).GetOrCreate("thaka:settlement:project-1", "settlement|100|cash"));

            Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void StocktakeCountIntentSurvivesServiceRecreationAndBindsTheCountPayload()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-operation-intent-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation-intents.json");
            var stocktakeId = Guid.CreateVersion7();
            var productId = Guid.CreateVersion7();
            var operationKey = $"count:{stocktakeId:D}:{productId:D}";
            var payload = $"{stocktakeId:D}|{productId:D}|12.5|shelf count";
            var originalId = new FileClientOperationIntentStore(path).GetOrCreate(operationKey, payload);

            var afterRestart = new FileClientOperationIntentStore(path);

            Assert.Equal(originalId, afterRestart.GetOrCreate(operationKey, payload));
            Assert.Throws<InvalidOperationException>(() =>
                afterRestart.GetOrCreate(operationKey, $"{stocktakeId:D}|{productId:D}|13|shelf count"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void StocktakeViewModelPreferredOperationIdIsRetainedByTheDurableIntent()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-operation-intent-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation-intents.json");
            const string operationKey = "stocktake:create";
            const string payload = "FullShop|Desktop full-shop stocktake";
            var viewModelOperationId = Guid.CreateVersion7();

            var idSentByViewModel = new FileClientOperationIntentStore(path)
                .GetOrCreate(operationKey, payload, viewModelOperationId);
            var idAcceptedByRecreatedService = new FileClientOperationIntentStore(path)
                .GetOrCreate(operationKey, payload, viewModelOperationId);

            Assert.Equal(viewModelOperationId, idSentByViewModel);
            Assert.Equal(idSentByViewModel, idAcceptedByRecreatedService);
            Assert.Throws<InvalidOperationException>(() =>
                new FileClientOperationIntentStore(path).GetOrCreate(operationKey, payload, Guid.CreateVersion7()));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void ExistingIntentStoreImplementationsCanKeepTheTwoArgumentContract()
    {
        IClientOperationIntentStore legacyImplementation = new LegacyIntentStore();
        var operationId = legacyImplementation.GetOrCreate("legacy-key", "legacy-payload");

        Assert.NotEqual(Guid.Empty, operationId);
    }

    private sealed class LegacyIntentStore : IClientOperationIntentStore
    {
        private readonly Dictionary<string, Guid> _entries = new(StringComparer.Ordinal);

        public Guid GetOrCreate(string operationKey, string payload)
        {
            _ = payload;
            if (!_entries.TryGetValue(operationKey, out var operationId))
            {
                operationId = Guid.CreateVersion7();
                _entries.Add(operationKey, operationId);
            }

            return operationId;
        }

        public void Complete(string operationKey, Guid operationId)
        {
            if (_entries.TryGetValue(operationKey, out var existing) && existing == operationId)
            {
                _entries.Remove(operationKey);
            }
        }
    }
}
