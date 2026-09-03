using Statesman;

StateKey<WeatherState> weatherKey = StateKey.Define<WeatherState>("weather/current");
StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("weather-api")
    .Container("weather", weather => weather
        .State(weatherKey, state => state
            .Initial(new WeatherState("unknown", 0))
            .Freshness(freshness => freshness.FreshFor(TimeSpan.FromMinutes(2)))
            .Load(load => load.From<IWeatherSource>("sensor", (source, _, cancellationToken) =>
                source.ReadAsync(cancellationToken)))
            .Refresh(refresh => refresh.OnFirstRead().WhenStale().Every(TimeSpan.FromMinutes(1)).OnSignal("weather.changed"))))
    .Build();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IWeatherSource, DemoWeatherSource>();
builder.Services.AddStatesman(declaration);
builder.Services.AddStatesmanHosting();
WebApplication app = builder.Build();

app.MapGet("/weather", async (IStatesman statesman, CancellationToken cancellationToken) =>
    (await statesman.State(weatherKey).GetAsync(StateReadOptions.Fresh, cancellationToken)).RequiredValue);
app.MapStatesman(options =>
{
    options.IncludeValuesByDefault = false;
    options.EnableSignals = app.Environment.IsDevelopment();
});
app.Run();

[ManagedState]
internal sealed record WeatherState(string Summary, int TemperatureFahrenheit);

internal interface IWeatherSource
{
    ValueTask<WeatherState> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class DemoWeatherSource : IWeatherSource
{
    public ValueTask<WeatherState> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new WeatherState("Clear", 72));
}

public partial class Program
{
}
