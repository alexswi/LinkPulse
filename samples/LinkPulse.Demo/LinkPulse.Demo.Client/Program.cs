using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The demo's authentication is handled entirely server-side: the dashboard, the badge, and the nav menu
// all render with InteractiveServer, and the only WASM-capable page (the stock Counter, render mode
// InteractiveAuto) needs no authorization. So there is nothing auth-related to wire up on the client.
await builder.Build().RunAsync();
