using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LibraryApi.Data;
using LibraryApi.Hubs;
using LibraryApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/notifications"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, UserIdProvider>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

    if (!await TableExistsAsync(db, "Users"))
    {
        if (await TableExistsAsync(db, "Books"))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Users" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                    "Email" TEXT NOT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "IsOffline" INTEGER NOT NULL DEFAULT 0,
                    "FullName" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                """);
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    if (!await TableExistsAsync(db, "BookLoans"))
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "BookLoans" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BookLoans" PRIMARY KEY AUTOINCREMENT,
                "BookId" INTEGER NOT NULL,
                "UserId" INTEGER NOT NULL,
                "BorrowedAt" TEXT NOT NULL,
                "DueDate" TEXT NOT NULL,
                "ReturnedAt" TEXT NULL,
                CONSTRAINT "FK_BookLoans_Books_BookId" FOREIGN KEY ("BookId") REFERENCES "Books" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_BookLoans_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """);
    }

    if (!await TableExistsAsync(db, "BookReservations"))
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "BookReservations" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BookReservations" PRIMARY KEY AUTOINCREMENT,
                "BookId" INTEGER NOT NULL,
                "UserId" INTEGER NOT NULL,
                "Position" INTEGER NOT NULL,
                "ReservedAt" TEXT NOT NULL,
                "NotifiedAt" TEXT NULL,
                "FulfilledAt" TEXT NULL,
                "CancelledAt" TEXT NULL,
                CONSTRAINT "FK_BookReservations_Books_BookId" FOREIGN KEY ("BookId") REFERENCES "Books" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_BookReservations_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_BookReservations_BookId_UserId"
                ON "BookReservations" ("BookId", "UserId")
                WHERE "CancelledAt" IS NULL AND "FulfilledAt" IS NULL;
            """);
    }

    await EnsureColumnExistsAsync(db, "Users", "IsOffline", "INTEGER NOT NULL DEFAULT 0");
    await EnsureColumnExistsAsync(db, "Users", "FullName", "TEXT NULL");
    await EnsureColumnExistsAsync(db, "BookLoans", "DueDate", "TEXT NOT NULL DEFAULT ''");

    // Tworzenie domyślnego konta administratora
    if (!await db.Users.AnyAsync())
    {
        db.Users.Add(new User
        {
            Email = "admin@library.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!"),
            Role = "Admin",
            FullName = "Administrator",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // Automatyczne wypełnianie bazy testowymi książkami
    if (!await db.Books.AnyAsync())
    {
        db.Books.AddRange(
            new Book
            {
                Title = "Wiedźmin: Ostatnie Życzenie",
                Author = "Andrzej Sapkowski",
                ISBN = "978-83-7578-063-5",
                IsAvailable = true
            },
            new Book
            {
                Title = "Lalka",
                Author = "Bolesław Prus",
                ISBN = "978-83-07-03123-1",
                IsAvailable = true
            },
            new Book
            {
                Title = "Pragmatyczny Programista",
                Author = "Andrew Hunt, David Thomas",
                ISBN = "978-83-283-9111-6",
                IsAvailable = false // Niedostępna na start - do testowania rezerwacji
            },
            new Book
            {
                Title = "Czysty Kod (Clean Code)",
                Author = "Robert C. Martin",
                ISBN = "978-83-246-2188-0",
                IsAvailable = true
            },
            new Book
            {
                Title = "Diuna",
                Author = "Frank Herbert",
                ISBN = "978-83-8188-250-7",
                IsAvailable = false // Niedostępna na start - do testowania rezerwacji
            }
        );
        await db.SaveChangesAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<NotificationHub>("/hubs/notifications").RequireAuthorization();

var auth = app.MapGroup("/api/auth").WithTags("Auth");

auth.MapPost("/register", async (RegisterRequest request, LibraryDbContext db) =>
{
    if (await db.Users.AnyAsync(u => u.Email == request.Email))
        return Results.Conflict(new { message = "Email already registered." });

    var user = new User
    {
        Email = request.Email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        FullName = request.FullName,
        Role = "User",
        IsOffline = false,
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Email, user.FullName, user.IsOffline, user.Role, user.CreatedAt });
});

auth.MapPost("/login", async (LoginRequest request, LibraryDbContext db, IConfiguration config) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = GenerateJwtToken(user, config);
    return Results.Ok(new { token, user.Email, user.FullName, user.Role, user.IsOffline });
});

var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization("AdminOnly");

users.MapGet("/", async (LibraryDbContext db, bool? isOffline) =>
{
    var query = db.Users.AsNoTracking();
    if (isOffline.HasValue)
        query = query.Where(u => u.IsOffline == isOffline.Value);

    var result = await query
        .Select(u => new { u.Id, u.Email, u.FullName, u.IsOffline, u.Role, u.CreatedAt })
        .ToListAsync();

    return Results.Ok(result);
});

users.MapPost("/offline", async (CreateOfflineUserRequest request, LibraryDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.FullName))
        return Results.BadRequest(new { message = "Full name is required for offline readers." });

    var email = !string.IsNullOrWhiteSpace(request.Email)
        ? request.Email
        : $"offline_{Guid.NewGuid().ToString("N")[..8]}@library.local";

    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { message = "Email/Identifier already registered." });

    var user = new User
    {
        Email = email,
        FullName = request.FullName,
        PasswordHash = string.Empty,
        Role = "User",
        IsOffline = true,
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Email, user.FullName, user.IsOffline, user.Role, user.CreatedAt });
});

var books = app.MapGroup("/api/books").WithTags("Books");

books.MapGet("/", async (LibraryDbContext db) =>
    await db.Books.AsNoTracking().ToListAsync());

books.MapGet("/{id:int}", async (int id, LibraryDbContext db) =>
{
    var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
    if (book is null)
        return Results.NotFound();

    var activeLoan = await db.BookLoans
        .AsNoTracking()
        .Include(l => l.User)
        .Where(l => l.BookId == id && l.ReturnedAt == null)
        .Select(l => new
        {
            l.Id,
            l.BorrowedAt,
            l.DueDate,
            isOverdue = l.DueDate < DateTime.UtcNow,
            borrower = new { l.User.Id, l.User.Email, l.User.FullName, l.User.IsOffline }
        })
        .FirstOrDefaultAsync();

    return Results.Ok(new
    {
        book.Id,
        book.Title,
        book.Author,
        book.ISBN,
        book.IsAvailable,
        activeLoan
    });
});

books.MapPost("/", async (Book book, LibraryDbContext db) =>
{
    db.Books.Add(book);
    await db.SaveChangesAsync();
    return Results.Created($"/api/books/{book.Id}", book);
}).RequireAuthorization("AdminOnly");

books.MapPut("/{id:int}", async (
    int id,
    Book updatedBook,
    LibraryDbContext db,
    IHubContext<NotificationHub> hubContext) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound();

    var wasAvailable = book.IsAvailable;

    book.Title = updatedBook.Title;
    book.Author = updatedBook.Author;
    book.ISBN = updatedBook.ISBN;
    book.IsAvailable = updatedBook.IsAvailable;

    BookReservation? reservationToNotify = null;
    if (!wasAvailable && book.IsAvailable)
    {
        reservationToNotify = await GetNextReservationAsync(db, book.Id);
        if (reservationToNotify is not null)
            reservationToNotify.NotifiedAt = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();

    if (reservationToNotify is not null)
        await SendBookAvailableNotificationAsync(hubContext, book, reservationToNotify);

    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

books.MapDelete("/{id:int}", async (int id, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound();

    db.Books.Remove(book);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// Web Client self-checkout (online user)
books.MapPost("/{id:int}/checkout", async (
    int id,
    LibraryDbContext db,
    IHubContext<NotificationHub> hubContext,
    ClaimsPrincipal user) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound();

    if (!book.IsAvailable)
        return Results.BadRequest(new { message = "Book is not available." });

    var borrower = await db.Users.FindAsync(userId.Value);
    if (borrower is null)
        return Results.Unauthorized();

    book.IsAvailable = false;

    var loan = new BookLoan
    {
        BookId = book.Id,
        UserId = userId.Value,
        BorrowedAt = DateTime.UtcNow,
        DueDate = DateTime.UtcNow.AddDays(14)
    };

    db.BookLoans.Add(loan);

    var reservation = await db.BookReservations
        .FirstOrDefaultAsync(r =>
            r.BookId == book.Id &&
            r.UserId == userId.Value &&
            r.CancelledAt == null &&
            r.FulfilledAt == null);

    if (reservation is not null)
        reservation.FulfilledAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    var message = $"Book '{book.Title}' has been checked out online by {borrower.FullName ?? borrower.Email}.";
    await hubContext.Clients.All.SendAsync("ReceiveNotification", message);

    return Results.Ok(new
    {
        book.Id,
        book.Title,
        book.Author,
        book.ISBN,
        book.IsAvailable,
        loan = new
        {
            loan.Id,
            loan.BorrowedAt,
            loan.DueDate,
            borrower = new { borrower.Id, borrower.Email, borrower.FullName, borrower.IsOffline }
        }
    });
}).RequireAuthorization();

// Desktop Librarian checkout (for online or offline reader)
books.MapPost("/{id:int}/checkout-admin", async (
    int id,
    AdminCheckoutRequest request,
    LibraryDbContext db,
    IHubContext<NotificationHub> hubContext) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound(new { message = "Book not found." });

    if (!book.IsAvailable)
        return Results.BadRequest(new { message = "Book is not available." });

    var borrower = await db.Users.FindAsync(request.UserId);
    if (borrower is null)
        return Results.BadRequest(new { message = "Borrower user not found." });

    book.IsAvailable = false;

    var loanDays = request.Days is > 0 ? request.Days.Value : 14;
    var loan = new BookLoan
    {
        BookId = book.Id,
        UserId = borrower.Id,
        BorrowedAt = DateTime.UtcNow,
        DueDate = DateTime.UtcNow.AddDays(loanDays)
    };

    db.BookLoans.Add(loan);

    var reservation = await db.BookReservations
        .FirstOrDefaultAsync(r =>
            r.BookId == book.Id &&
            r.UserId == borrower.Id &&
            r.CancelledAt == null &&
            r.FulfilledAt == null);

    if (reservation is not null)
        reservation.FulfilledAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    var message = $"Book '{book.Title}' checked out to {borrower.FullName ?? borrower.Email} by Librarian.";
    await hubContext.Clients.All.SendAsync("ReceiveNotification", message);

    return Results.Ok(new
    {
        book.Id,
        book.Title,
        book.Author,
        book.ISBN,
        book.IsAvailable,
        loan = new
        {
            loan.Id,
            loan.BorrowedAt,
            loan.DueDate,
            borrower = new { borrower.Id, borrower.Email, borrower.FullName, borrower.IsOffline }
        }
    });
}).RequireAuthorization("AdminOnly");

books.MapPost("/{id:int}/return", async (
    int id,
    LibraryDbContext db,
    IHubContext<NotificationHub> hubContext,
    ClaimsPrincipal user) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound();

    var activeLoan = await db.BookLoans
        .Include(l => l.User)
        .FirstOrDefaultAsync(l => l.BookId == id && l.ReturnedAt == null);

    if (activeLoan is null)
        return Results.BadRequest(new { message = "Book is not currently borrowed." });

    var isAdmin = user.IsInRole("Admin");
    if (!isAdmin && activeLoan.UserId != userId.Value)
        return Results.Forbid();

    activeLoan.ReturnedAt = DateTime.UtcNow;
    book.IsAvailable = true;

    var reservationToNotify = await GetNextReservationAsync(db, book.Id);
    if (reservationToNotify is not null)
        reservationToNotify.NotifiedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    if (reservationToNotify is not null)
        await SendBookAvailableNotificationAsync(hubContext, book, reservationToNotify);

    return Results.Ok(new
    {
        book.Id,
        book.Title,
        book.IsAvailable,
        loan = new
        {
            activeLoan.Id,
            activeLoan.BorrowedAt,
            activeLoan.DueDate,
            activeLoan.ReturnedAt,
            borrower = new { activeLoan.User.Id, activeLoan.User.Email, activeLoan.User.FullName, activeLoan.User.IsOffline }
        }
    });
}).RequireAuthorization();

books.MapPost("/{id:int}/reserve", async (int id, LibraryDbContext db, ClaimsPrincipal user) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return Results.NotFound();

    if (book.IsAvailable)
        return Results.BadRequest(new { message = "Book is available. Check it out instead of reserving." });

    var existingReservation = await db.BookReservations
        .FirstOrDefaultAsync(r =>
            r.BookId == id &&
            r.UserId == userId.Value &&
            r.CancelledAt == null &&
            r.FulfilledAt == null);

    if (existingReservation is not null)
        return Results.Conflict(new { message = "You already have an active reservation for this book." });

    var position = await db.BookReservations
        .Where(r => r.BookId == id && r.CancelledAt == null && r.FulfilledAt == null)
        .CountAsync() + 1;

    var reservation = new BookReservation
    {
        BookId = book.Id,
        UserId = userId.Value,
        Position = position,
        ReservedAt = DateTime.UtcNow
    };

    db.BookReservations.Add(reservation);
    await db.SaveChangesAsync();

    return Results.Created($"/api/reservations/{reservation.Id}", new
    {
        reservation.Id,
        reservation.Position,
        reservation.ReservedAt,
        book = new { book.Id, book.Title, book.Author, book.ISBN, book.IsAvailable }
    });
}).RequireAuthorization();

books.MapDelete("/{id:int}/reserve", async (int id, LibraryDbContext db, ClaimsPrincipal user) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var reservation = await db.BookReservations
        .FirstOrDefaultAsync(r =>
            r.BookId == id &&
            r.UserId == userId.Value &&
            r.CancelledAt == null &&
            r.FulfilledAt == null);

    if (reservation is null)
        return Results.NotFound();

    reservation.CancelledAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization();

var loans = app.MapGroup("/api/loans").WithTags("Loans").RequireAuthorization();

loans.MapGet("/", async (LibraryDbContext db, ClaimsPrincipal user, bool? active, bool? overdue) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var query = db.BookLoans
        .AsNoTracking()
        .Include(l => l.Book)
        .Include(l => l.User)
        .AsQueryable();

    if (!user.IsInRole("Admin"))
        query = query.Where(l => l.UserId == userId.Value);

    if (active == true)
        query = query.Where(l => l.ReturnedAt == null);
    else if (active == false)
        query = query.Where(l => l.ReturnedAt != null);

    if (overdue == true)
    {
        var now = DateTime.UtcNow;
        query = query.Where(l => l.ReturnedAt == null && l.DueDate < now);
    }

    var nowUtc = DateTime.UtcNow;
    var results = await query
        .OrderByDescending(l => l.BorrowedAt)
        .Select(l => new
        {
            l.Id,
            l.BorrowedAt,
            l.DueDate,
            l.ReturnedAt,
            isOverdue = l.ReturnedAt == null && l.DueDate < nowUtc,
            daysOverdue = (l.ReturnedAt == null && l.DueDate < nowUtc) ? (int)(nowUtc - l.DueDate).TotalDays : 0,
            book = new { l.Book.Id, l.Book.Title, l.Book.Author, l.Book.ISBN },
            borrower = new { l.User.Id, l.User.Email, l.User.FullName, l.User.IsOffline }
        })
        .ToListAsync();

    return Results.Ok(results);
});

loans.MapGet("/overdue", async (LibraryDbContext db) =>
{
    var now = DateTime.UtcNow;
    var overdueLoans = await db.BookLoans
        .AsNoTracking()
        .Include(l => l.Book)
        .Include(l => l.User)
        .Where(l => l.ReturnedAt == null && l.DueDate < now)
        .OrderBy(l => l.DueDate)
        .Select(l => new
        {
            l.Id,
            l.BorrowedAt,
            l.DueDate,
            daysOverdue = (int)(now - l.DueDate).TotalDays,
            book = new { l.Book.Id, l.Book.Title, l.Book.Author, l.Book.ISBN },
            borrower = new { l.User.Id, l.User.Email, l.User.FullName, l.User.IsOffline }
        })
        .ToListAsync();

    return Results.Ok(overdueLoans);
}).RequireAuthorization("AdminOnly");

var reservations = app.MapGroup("/api/reservations").WithTags("Reservations").RequireAuthorization();

reservations.MapGet("/", async (LibraryDbContext db, ClaimsPrincipal user) =>
{
    var userId = GetUserId(user);
    if (userId is null)
        return Results.Unauthorized();

    var results = await db.BookReservations
        .AsNoTracking()
        .Include(r => r.Book)
        .Where(r => r.UserId == userId.Value && r.CancelledAt == null && r.FulfilledAt == null)
        .OrderBy(r => r.ReservedAt)
        .Select(r => new
        {
            r.Id,
            r.Position,
            r.ReservedAt,
            r.NotifiedAt,
            book = new { r.Book.Id, r.Book.Title, r.Book.Author, r.Book.ISBN, r.Book.IsAvailable }
        })
        .ToListAsync();

    return Results.Ok(results);
});

reservations.MapGet("/admin", async (LibraryDbContext db) =>
{
    var results = await db.BookReservations
        .AsNoTracking()
        .Include(r => r.Book)
        .Include(r => r.User)
        .Where(r => r.CancelledAt == null && r.FulfilledAt == null)
        .OrderBy(r => r.BookId)
        .ThenBy(r => r.Position)
        .Select(r => new
        {
            r.Id,
            r.BookId,
            r.UserId,
            r.Position,
            r.ReservedAt,
            r.NotifiedAt,
            book = new { r.Book.Id, r.Book.Title, r.Book.Author, r.Book.ISBN, r.Book.IsAvailable },
            borrower = new { r.User.Id, r.User.Email, r.User.FullName, r.User.IsOffline }
        })
        .ToListAsync();

    return Results.Ok(results);
}).RequireAuthorization("AdminOnly");

reservations.MapDelete("/admin/{id:int}", async (int id, LibraryDbContext db) =>
{
    var reservation = await db.BookReservations.FindAsync(id);
    if (reservation is null)
        return Results.NotFound();

    reservation.CancelledAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.Run();

static async Task<bool> TableExistsAsync(LibraryDbContext db, string tableName)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;

    if (shouldClose)
        await connection.OpenAsync();

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }
    finally
    {
        if (shouldClose)
            await connection.CloseAsync();
    }
}

static async Task EnsureColumnExistsAsync(LibraryDbContext db, string tableName, string columnName, string columnDefinition)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != System.Data.ConnectionState.Open;

    if (shouldClose)
        await connection.OpenAsync();

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        await using var reader = await command.ExecuteReaderAsync();
        var exists = false;
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
        await reader.CloseAsync();

        if (!exists)
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};";
            await alterCmd.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        if (shouldClose)
            await connection.CloseAsync();
    }
}

static int? GetUserId(ClaimsPrincipal user)
{
    var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
    return int.TryParse(id, out var userId) ? userId : null;
}

static Task<BookReservation?> GetNextReservationAsync(LibraryDbContext db, int bookId) =>
    db.BookReservations
        .Where(r => r.BookId == bookId && r.CancelledAt == null && r.FulfilledAt == null && r.NotifiedAt == null)
        .OrderBy(r => r.Position)
        .FirstOrDefaultAsync();

static Task SendBookAvailableNotificationAsync(
    IHubContext<NotificationHub> hubContext,
    Book book,
    BookReservation reservation) =>
    hubContext.Clients
        .User(reservation.UserId.ToString())
        .SendAsync("BookAvailable", new
        {
            reservationId = reservation.Id,
            position = reservation.Position,
            bookId = book.Id,
            title = book.Title,
            author = book.Author,
            isbn = book.ISBN,
            message = $"Your reserved book '{book.Title}' is now available."
        });

static string GenerateJwtToken(User user, IConfiguration config)
{
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role)
    };

    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"],
        audience: config["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: credentials);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

record RegisterRequest(string Email, string Password, string? FullName);
record CreateOfflineUserRequest(string FullName, string? Email);
record LoginRequest(string Email, string Password);
record AdminCheckoutRequest(int UserId, int? Days);