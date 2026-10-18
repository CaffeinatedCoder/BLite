using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;

namespace BLite.Shared;

// Regression fixtures for EntglDb/BLite#300: the mapper generator only looked at classes
// whose name ends in "Context", so a DocumentDbContext called anything else got no generated
// code and no diagnostic, and its collections stayed null. These classes compiling at all
// (InitializeCollections is generated) already proves the generator found them.

/// <summary>A context whose name does not end in "Context".</summary>
public partial class Shop : DocumentDbContext
{
    public DocumentCollection<ObjectId, ShopCustomer> Customers { get; set; } = null!;

    public Shop(string databasePath) : base(databasePath)
    {
        InitializeCollections();
    }
}

/// <summary>A context that derives from another context, neither named with the suffix.</summary>
public partial class OnlineShop : Shop
{
    public DocumentCollection<ObjectId, ShopOrder> Orders { get; set; } = null!;

    public OnlineShop(string databasePath) : base(databasePath)
    {
        InitializeCollections();
    }
}

public class ShopCustomer
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
}

public class ShopOrder
{
    public ObjectId Id { get; set; }
    public decimal Total { get; set; }
}
