using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LibraryApi.Hubs;

[Authorize]
public class NotificationHub : Hub
{
}
