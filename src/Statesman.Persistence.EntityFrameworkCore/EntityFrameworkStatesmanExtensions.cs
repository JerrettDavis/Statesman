using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Statesman;

public static class EntityFrameworkStatesmanExtensions
{
    public static StatesmanServiceBuilder UseEntityFrameworkStore<TContext>(
        this StatesmanServiceBuilder builder,
        string name)
        where TContext : StatesmanLedgerDbContext
    {
        return builder.UseStore(name, services => new EntityFrameworkStateLedgerStore<TContext>(
            name,
            services.GetRequiredService<IDbContextFactory<TContext>>(),
            services.GetService<TimeProvider>()));
    }
}
