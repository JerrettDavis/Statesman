using Statesman;
using Statesman.Testing;

StateKey<UserState> user = StateKey.Define<UserState>("users/current");
StateKey<CartState> cart = StateKey.Define<CartState>("checkout/cart");

StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("storefront-tests")
    .State(user, _ => { })
    .State(cart, _ => { })
    .Build();

// Scenario A: describe a test's starting state inline.
await using StatesmanTestHarness firstRun = StatesmanTestHarness.Create(declaration);
await firstRun.Seed()
    .Source("scenario:member-with-cart")
    .State(user, new UserState("user-42", "JD", true))
    .State(cart, new CartState(new[] { "sku-1", "sku-2" }, 2))
    .ApplyAsync();

StateFixture fixture = await firstRun.Runtime.CaptureFixtureAsync(new[]
{
    new StateReference(user.Path),
    new StateReference(cart.Path),
});

await fixture.SaveAsync("artifacts/member-with-cart.statesman.json");
Console.WriteLine(fixture.ToJson());

// Scenario B: a later test/process starts from the exact same logical state.
await using StatesmanTestHarness repeatedRun = StatesmanTestHarness.Create(declaration);
StateFixture saved = await StateFixtureExtensions.LoadFixtureAsync("artifacts/member-with-cart.statesman.json");
await repeatedRun.ApplyFixtureAsync(saved);

Console.WriteLine(repeatedRun.Runtime.State(user).Current.RequiredValue);
Console.WriteLine(repeatedRun.Runtime.State(cart).Current.RequiredValue);

internal sealed record UserState(string Id, string Name, bool IsMember);
internal sealed record CartState(IReadOnlyList<string> Skus, int Quantity);
