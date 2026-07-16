using LiteCircuit.Core.Pcb;

namespace LiteCircuit.Infrastructure.Services;

/// <summary>
/// Grid-based Manhattan auto-router (Lee/BFS wave expansion) on the top copper layer,
/// falling back to the bottom layer when the top is blocked. Intended as the fast
/// baseline the AI placement/routing assistant refines.
/// </summary>
public class AutoRouterService
{
    private const double Step = 0.5;   // routing grid in mm

    public (Board Board, string Report) Route(Board b)
    {
        int nx = Math.Max(2, (int)(b.Width / Step)), ny = Math.Max(2, (int)(b.Height / Step));
        if ((long)nx * ny > 1_000_000) return (b, "Board too large for the built-in grid router.");

        // Obstacle grids per routable layer: cell -> owning net ("" = free, "*" = keepout)
        var layers = new[] { "F.Cu", "B.Cu" }.Where(l => b.CopperLayers.Any(cl => cl.Name == l)).ToArray();
        var grid = layers.ToDictionary(l => l, _ => CreateGrid(nx, ny));
        var clearanceCells = Math.Max(1, (int)Math.Ceiling(b.Rules.MinClearance / Step));

        foreach (var c in b.Components)
            foreach (var p in c.Pads)
            {
                var (px, py) = p.AbsoluteOn(c);
                var rad = (int)Math.Ceiling((Math.Max(p.W, p.H) / 2 + b.Rules.MinClearance) / Step);
                foreach (var l in p.Drill > 0 ? layers : layers.Take(1)) // TH pads block both layers
                    Stamp(grid[l], nx, ny, px, py, rad, string.IsNullOrEmpty(p.Net) ? "*" : p.Net);
            }
        foreach (var t in b.Tracks) // existing manual tracks are obstacles
            foreach (var seg in Pairwise(t.Points))
                StampSegment(grid.GetValueOrDefault(t.Layer), nx, ny, seg, clearanceCells, t.Net);

        var routedCount = 0;
        var failed = new List<string>();

        foreach (var net in b.Nets.Where(n => !string.IsNullOrEmpty(n)))
        {
            var padPts = b.Components
                .SelectMany(c => c.Pads.Where(p => p.Net == net).Select(p => p.AbsoluteOn(c)))
                .ToList();
            if (padPts.Count < 2) continue;
            if (b.Tracks.Any(t => t.Net == net)) continue; // already routed manually

            // Connect pads as a chain: each pad to the nearest already-connected point.
            var connected = new List<(double X, double Y)> { padPts[0] };
            var pending = padPts.Skip(1).ToList();
            var ok = true;

            while (pending.Count > 0)
            {
                var next = pending.OrderBy(p => connected.Min(c => Sq(c, p))).First();
                pending.Remove(next);
                var from = connected.OrderBy(c => Sq(c, next)).First();

                var routedOnLayer = false;
                foreach (var layer in layers)
                {
                    var path = Bfs(grid[layer], nx, ny, ToCell(from), ToCell(next), net);
                    if (path is null) continue;
                    var pts = Simplify(path).Select(cell => new[]
                    {
                        Math.Round(cell.x * Step + Step / 2, 3),
                        Math.Round(cell.y * Step + Step / 2, 3),
                    }).ToList();
                    // snap endpoints exactly onto the pads
                    pts[0] = new[] { from.X, from.Y };
                    pts[^1] = new[] { next.X, next.Y };
                    b.Tracks.Add(new Track { Net = net, Layer = layer, Width = 0.25, Points = pts });
                    foreach (var cell in path) grid[layer][cell.y * nx + cell.x] = net;
                    routedOnLayer = true;
                    break;
                }
                if (!routedOnLayer) { ok = false; break; }
                connected.Add(next);
            }

            if (ok) routedCount++; else failed.Add(net);
        }

        var report = $"Auto-router: {routedCount} net(s) routed on grid {Step}mm." +
                     (failed.Count > 0 ? $" Could not fully route: {string.Join(", ", failed)}." : " All routable nets completed.");
        return (b, report);

        (int x, int y) ToCell((double X, double Y) p) =>
            (Math.Clamp((int)(p.X / Step), 0, nx - 1), Math.Clamp((int)(p.Y / Step), 0, ny - 1));
    }

    private static string[] CreateGrid(int nx, int ny)
    {
        var g = new string[nx * ny];
        Array.Fill(g, "");
        return g;
    }

    private static double Sq((double X, double Y) a, (double X, double Y) b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static IEnumerable<(double[] A, double[] B)> Pairwise(List<double[]> pts)
    {
        for (var i = 0; i + 1 < pts.Count; i++) yield return (pts[i], pts[i + 1]);
    }

    private static void Stamp(string[] g, int nx, int ny, double x, double y, int rad, string net)
    {
        int cx = (int)(x / Step), cy = (int)(y / Step);
        for (var dy = -rad; dy <= rad; dy++)
            for (var dx = -rad; dx <= rad; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (px >= 0 && py >= 0 && px < nx && py < ny) g[py * nx + px] = net;
            }
    }

    private static void StampSegment(string[]? g, int nx, int ny, (double[] A, double[] B) seg, int rad, string net)
    {
        if (g is null) return;
        var steps = (int)(Math.Max(Math.Abs(seg.B[0] - seg.A[0]), Math.Abs(seg.B[1] - seg.A[1])) / Step) + 1;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            Stamp(g, nx, ny, seg.A[0] + t * (seg.B[0] - seg.A[0]), seg.A[1] + t * (seg.B[1] - seg.A[1]), rad, net);
        }
    }

    private static List<(int x, int y)>? Bfs(string[] g, int nx, int ny, (int x, int y) start, (int x, int y) goal, string net)
    {
        bool Free(int x, int y)
        {
            var o = g[y * nx + x];
            return o == "" || o == net;
        }
        if (!Free(goal.x, goal.y) || !Free(start.x, start.y)) return null;

        var prev = new int[nx * ny];
        Array.Fill(prev, -2);
        prev[start.y * nx + start.x] = -1;
        var q = new Queue<(int x, int y)>();
        q.Enqueue(start);
        Span<(int dx, int dy)> dirs = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            if ((x, y) == goal) break;
            foreach (var (dx, dy) in dirs)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= nx || py >= ny) continue;
                var idx = py * nx + px;
                if (prev[idx] != -2 || !Free(px, py)) continue;
                prev[idx] = y * nx + x;
                q.Enqueue((px, py));
            }
        }

        if (prev[goal.y * nx + goal.x] == -2) return null;
        var path = new List<(int x, int y)>();
        var cur = goal.y * nx + goal.x;
        while (cur != -1)
        {
            path.Add((cur % nx, cur / nx));
            cur = prev[cur];
        }
        path.Reverse();
        return path;
    }

    private static List<(int x, int y)> Simplify(List<(int x, int y)> path)
    {
        if (path.Count <= 2) return path;
        var outp = new List<(int x, int y)> { path[0] };
        for (var i = 1; i + 1 < path.Count; i++)
        {
            var (a, b, c) = (path[i - 1], path[i], path[i + 1]);
            if ((b.x - a.x) * (c.y - b.y) != (b.y - a.y) * (c.x - b.x)) outp.Add(b);
        }
        outp.Add(path[^1]);
        return outp;
    }
}
