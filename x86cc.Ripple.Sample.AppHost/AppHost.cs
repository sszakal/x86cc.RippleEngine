var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddPostgres("postgres", port: 55432)
    .WithDataVolume("ripple-pgdata")
    .AddDatabase("ripple");

// Pin the FRONT (proxy) ports so 5100/5200 are stable addresses the Angular dashboard's dev proxy
// (proxy.conf.json → :5200) and any bookmarks can rely on. We pin `port` only, NOT `targetPort`: the
// worker runs 3 replicas, and Aspire's proxy load-balances the single front port across each replica's
// OWN (auto-assigned) target port — pinning targetPort would make the three replicas collide. So each
// replica's console log shows its internal target port, while 5100/5200 stay the reachable front doors.
builder.AddProject<Projects.x86cc_Ripple_Sample_WebAPI>("webapi")
    .WithReference(db)
    .WithHttpEndpoint(port: 5100, name: "dashboard")
    .WithExternalHttpEndpoints()
    .WaitFor(db);

builder.AddProject<Projects.x86cc_Ripple_Sample_Worker>("worker")
    .WithReference(db)
    .WithHttpEndpoint(port: 5200, name: "dashboard")
    .WithExternalHttpEndpoints()
    .WaitFor(db)
    .WithReplicas(3);

builder.Build().Run();
