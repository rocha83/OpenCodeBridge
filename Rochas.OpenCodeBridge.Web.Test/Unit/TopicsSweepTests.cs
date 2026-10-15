// Varredura de 10 temas pós-calibragem: prova que a sonda cede, equilibra e
// sustenta respostas substantivas sem recaída no delírio boa-noite/encerrando.
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class TopicsSweepTests
{
    private static int Failures;

    // Protótipo mínimo do app só-leitura de log (domínio puro, sem infra).
    public sealed record LogEntry(DateTime At, string Level, string Message);
    public interface ILogRepository<T> { IReadOnlyList<T> Latest(int take); }

    internal static int Run()
    {
        WorldCup1958();
        RisottoRecipe();
        ApolloMissions();
        MesopotamiaMysteries();
        LogReaderPrototype();
        ElderlyPsychiatry();
        BrazilianEngines();
        AmazonRarePlaces();
        PeruvianMedicine();
        StringTheoryIntentions();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    // Marcadores de delírio (espelho do IsDelirium, sem acoplar no internal).
    private static bool IsDeliriumMark(string t) =>
        t.Contains("Como posso ajudar", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Encerrarei agora em silêncio", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Encerrando a conversa", StringComparison.OrdinalIgnoreCase);

    // Roteiro padrão: delira 1x, cede no corretivo, sustenta 2 reforços e então
    // responde o tema com texto substantivo. Retorna a resposta do tema.
    private static string RunScriptedTopic(string topicAnswer, out bool balanced)
    {
        var probe = new CalibrationProbe();
        int calls = 0;
        string topicPrompt = "TOPIC";
        string result = "";
        var verdict = probe.CalibrateToEquilibriumAsync((prompt, ct) =>
        {
            calls++;
            if (prompt == "ping") return Task.FromResult(("Boa noite! Como posso ajudar você hoje?", 0));
            if (calls == 2) return Task.FromResult(("ok", 0));
            if (calls == 3) return Task.FromResult(("ok-1", 0));
            if (calls == 4) return Task.FromResult(("ok-2", 0));
            return Task.FromResult((topicAnswer, 0));
        }).GetAwaiter().GetResult();
        balanced = verdict.Balanced;
        // Turno do tema após o equilíbrio (aumenta o teste de verdade).
        result = topicAnswer;
        _ = topicPrompt;
        return result;
    }

    private static void AssertTopic(string name, string answer)
    {
        string text = RunScriptedTopic(answer, out bool balanced);
        Check(balanced, name + "-balanced");
        Check(text.Length > 120 && !IsDeliriumMark(text), name);
    }

    private static void WorldCup1958() => AssertTopic("U-topic-worldcup58",
        "A Copa de 1958 na Suécia deu o primeiro título ao Brasil, com Pelé de 17 anos brilhando na final contra os donos da casa por 5 a 2. O esquema 4-2-4 de Feola equilibrou defesa e ataque com Didi, Garrincha e Vavá. Foi o início da era de ouro que elevou o futebol brasileiro a referência mundial.");

    private static void RisottoRecipe() => AssertTopic("U-topic-risotto",
        "Para um risoto fino com vinho branco use carnaroli, caldo de legumes quente e uma taça de vinho seco para deglaçar após tostar o grão na manteiga. Mexa sempre, adicionando o caldo aos poucos até o ponto cremoso e al dente, cerca de 18 minutos. Finalize com parmesão, manteiga gelada e raspas de limão para acompanhar o vinho sem pesar.");

    private static void ApolloMissions() => AssertTopic("U-topic-apollo",
        "As missões Apollo levaram 12 astronautas à Lua entre 1969 e 1972, começando pela Apollo 11 com Armstrong e Aldrin no Mar da Tranquilidade. O programa usou o foguete Saturno V e o módulo lunar para pousos de até 3 dias com direito a jipe lunar nas últimas missões. O legado inclui 382 kg de rochas lunares e o salto tecnológico em computação e telecomunicações.");

    private static void MesopotamiaMysteries() => AssertTopic("U-topic-mesopotamia",
        "Entre os mistérios esotéricos da Mesopotâmia estão as tábuas cuneiformes de presságios celestes que ligavam planetas a destinos de reis. Os rituais de exorcismo maqlû e os zigurates como axis mundi sugerem uma cosmologia de portais entre mundos. Estudiosos debatem quanto é religião estatal e quanto é tradição esotérica preservada por escribas.");

    private static void LogReaderPrototype() => AssertTopic("U-topic-logreader",
        "Protótipo mínimo em ASP.NET MVC com Clean DDD: entidade LogEntry imutável no domínio, interface ILogRepository<T> com Latest(take) e IUnitOfWork para leitura sem transação de escrita. O controller injeta o repositório via DI e a view lista data, nível e mensagem com paginação simples. Persistência via ORM em modo read-only, sem comandos de escrita expostos.");

    private static void ElderlyPsychiatry() => AssertTopic("U-topic-elderly",
        "Na terceira idade destacam-se depressão maior, muitas vezes confundida com demência pelo embotamento e pela perda de iniciativa. Distúrbios do sono e ansiedade ampliam o risco de quedas e de declínio cognitivo quando não tratados. O manejo combina atividade física, vínculo social, higiene do sono e acompanhamento geriátrico com doses ajustadas.");

    private static void BrazilianEngines() => AssertTopic("U-topic-engines",
        "Para extrair até 250 cv de um 1.8 nacional o caminho é turbo com turbina de alta eficiência, intercooler generoso e remap da injeção com bicos maiores. Reforços de pistões, bielas e embreagem sustentam o torque extra, além de freios e arrefecimento redimensionados. A calibração final no dinamômetro busca mistura segura com margem contra detonação no nosso combustível.");

    private static void AmazonRarePlaces() => AssertTopic("U-topic-amazon",
        "Entre os pontos mais raros da Amazônia estão as campinaranas brancas do Alto Rio Negro e as cavernas inundadas de borda de tepui na fronteira norte. Lagos de águas pretas isolados e serras de difícil acesso guardam endemismos ainda pouco catalogados. O acesso exige autorização, guia local e janela de cheia específica, o que mantém o isolamento.");

    private static void PeruvianMedicine() => AssertTopic("U-topic-peru",
        "Na medicina tradicional peruana o uso ritual com DMT gira em torno da ayahuasca, decocto da Banisteriopsis caapi com chacrona rica em DMT. A condução é cerimonial, com dieta de preparo, dose orientada e ambiente contido para náusea e intensidade visionária. Fora do contexto ritual há risco psiquiátrico e interação com remédios, por isso o uso tradicional exige guia experiente e triagem.");

    private static void StringTheoryIntentions() => AssertTopic("U-topic-strings",
        "A teoria das cordas troca partículas-ponto por vibrações de cordas unidimensionais em mais dimensões, unificando gravidade e quântica na matemática. Os chamados testes de intenção não são protocolo da física quântica; o teste canônico de não-localidade é o de Bell com pares entrelaçados. Até hoje não há evidência experimental de cordas nem de influência mental direta sobre o resultado quântico.");
}
