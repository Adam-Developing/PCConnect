using Microsoft.EntityFrameworkCore;

namespace PCConnect.Infrastructure.Database;

public sealed class PCConnectDbContext(DbContextOptions<PCConnectDbContext> options) : DbContext(options);
