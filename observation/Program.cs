using System.Diagnostics;
using Serilog;

namespace OpenCodeBridge.Observation;

internal static class Program
{
    static async Task Main(string[] args)
    {
        int port = 4800;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);

        var logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("observation.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        logger.Information("Subindo observacao Grafana na porta {Port}", port);

        // Inicia o Grafana se necessario.
        try
        {
            var check = Run("curl", $"-sf -m 2 http://localhost:{port}/api/health", 5);
            if (check != 0)
            {
                logger.Information("Grafana inativo; iniciando servico");
                Run("sudo", "systemctl start grafana-server", 30);
            }
        }
        catch (Exception ex) { logger.Warning("falha ao verificar grafana: {Error}", ex.Message); }

        // Provisiona dashboard (copia para o diretorio do Grafana).
        string repo = Environment.CurrentDirectory;
        string dashSrc = Path.Combine(repo, "observation", "grafana", "dashboards", "opencodebridge.json");
        string dashDst = "/var/lib/grafana/dashboards/opencodebridge.json";
        string provSrc = Path.Combine(repo, "observation", "grafana", "provisioning", "dashboards", "opencodebridge.yaml");
        string provDst = "/etc/grafana/provisioning/dashboards/opencodebridge.yaml";
        string dsSrc = Path.Combine(repo, "observation", "grafana", "provisioning", "datasources", "opencodebridge.yaml");
        string dsDst = "/etc/grafana/provisioning/datasources/opencodebridge.yaml";
        if (File.Exists(dashSrc))
        {
            try
            {
                Run("sudo", $"cp {dashSrc} {dashDst}", 10);
                Run("sudo", $"cp {provSrc} {provDst}", 10);
                if (File.Exists(dsSrc)) Run("sudo", $"cp {dsSrc} {dsDst}", 10);
                Run("sudo", "systemctl restart grafana-server", 30);
                logger.Information("Dashboard provisionado em {Path}", dashDst);
            }
            catch (Exception ex) { logger.Warning("provisionamento falhou: {Error}", ex.Message); }
        }

        // Espera health.
        for (int i = 0; i < 30; i++)
        {
            if (Run("curl", $"-sf -m 2 http://localhost:{port}/api/health", 3) == 0) break;
            await Task.Delay(2000);
        }

        string url = $"http://localhost:{port}/d/opencodebridge/opencodebridge-observation";
        logger.Information("Abrindo navegador em {Url}", url);
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = true }); }
        catch (Exception ex) { logger.Warning("nao foi possivel abrir navegador: {Error}", ex.Message); }

        logger.Information("Observacao ativa. Ctrl+C para encerrar.");
        await Task.Delay(Timeout.Infinite);
    }

    static int Run(string exe, string args, int seconds)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(true); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
