# Rochas.AI.Manager — Documento de Arquitetura e Implementação (Guideline de Desenvolvimento v1.2)

> **Revisão 2026-10-20:** este guideline permanece como referência de intenção (v1). O estado real do código está registrado na **seção 32 (as-built)** ao final do documento. A arquitetura-alvo revisada, já incorporando o as-built, está em `Rochas.AI.Manager-Arquitetura v2.md`; o catálogo e as pendências de relatórios estão em `REPORT-PENDINGS.md`.

## 1. Visão geral do projeto

O Rochas.AI.Manager será desenvolvido sobre a base funcional existente do Gemma Forge, preservando o chat web, a bridge de compatibilidade com OpenCode e os componentes que já funcionam. O objetivo é evoluir o projeto existente para uma plataforma de gerenciamento, automação e treinamento de modelos de inteligência artificial, com foco inicial no Gemma 4 12B, porém preparada para suportar, além do Gemma 4, modelos como Llama, Qwen e DeepSeek. A aplicação será desenvolvida integralmente em C#, sem dependência de Python no produto final, utilizando TorchSharp como base numérica para a implementação própria do motor de treinamento, e integrando-se a servidores de inferência locais como Ollama e llama turboquant servers. A plataforma não deve ser reconstruída do zero. A estratégia é reutilizar e adaptar a estrutura existente, evitando duplicação de funcionalidades, quebra de integrações e alterações desnecessárias na bridge. Um diferencial central é a experiência de treinamento "doméstica": o usuário comum não precisa lidar com pesos, gradientes ou hiperparâmetros granulares. Em vez disso, escolhe um perfil de instrução (científico, jurídico, corporativo, engenheiro de software, entre outros) e o sistema monta o conjunto de instruções adequado de forma implícita. Um modo avançado permanece disponível para usuários que desejam controle fino.

## 2. Requisitos tecnológicos

- **Nome do produto:** Rochas.AI.Manager.
- **Base funcional:** Gemma Forge existente.
- **Linguagem principal:** C#.
- **Runtime:** .NET 10 LTS, sujeito à compatibilidade dos componentes existentes.
- **Interface:** ASP.NET Core MVC com Razor Views.
- **Interatividade:** JavaScript puro e AJAX utilizando fetch.
- **Backend:** ASP.NET Core.
- **Arquitetura:** Domain-Driven Design (DDD).
- **Persistência:** Rochas.DapperRepository (Dapper) no lugar do Entity Framework Core.
- **ORM:** Rochas.DapperRepository (ORM completa baseada em Dapper).
- **Componentes próprios:** Rochas.ImageGenerator, Rochas.PDFGenerator, Rochas.DapperRepository, Rochas.CacheIndexer.
- **Repositórios:** https://github.com/rocha83 (perfil dos componentes).
- **Padrões de acesso a dados:** Generic Repository e Unit of Work.
- **Banco inicial:** SQLite, com possibilidade de PostgreSQL futuramente.
- **Motor numérico:** TorchSharp com LibTorch e CUDA compatíveis.
- **Hardware:** GPU NVIDIA.
- **Inferência:** integração com servidores locais: Ollama e llama turboquant servers.
- **Modelos suportados:** Gemma 4, Llama, Qwen, DeepSeek (e outros compatíveis).
- **Treinamento:** implementação própria em C#, sem Python no produto final.
- **Testes:** xUnit.
- **Monitoramento:** logs estruturados, métricas e atualização de estado por AJAX ou SignalR.
- **Integração:** preservar a bridge de compatibilidade com OpenCode e o chat web existente.

A compatibilidade de versões do .NET, TorchSharp, LibTorch, CUDA, drivers NVIDIA e dos servidores de inferência deverá ser verificada antes de fixar as dependências definitivas.

## 3. Objetivos funcionais

A plataforma deverá permitir:

- Utilizar o chat web existente.
- Preservar a bridge que transforma e encaminha conteúdo compatível com OpenCode.
- Gerenciar modelos-base e checkpoints de múltiplos modelos (Gemma 4, Llama, Qwen, DeepSeek).
- Detectar e avaliar os recursos de hardware disponíveis.
- Importar, validar e versionar datasets.
- Configurar experimentos de fine-tuning em modo básico (pragmático) ou avançado (granular).
- Selecionar automaticamente estratégias de treinamento compatíveis com o hardware.
- Executar treinamentos em segundo plano.
- Monitorar métricas, loss, consumo de memória e progresso.
- Cancelar e retomar jobs quando tecnicamente possível.
- Gerenciar checkpoints e adaptadores LoRA.
- Comparar o modelo-base com versões ajustadas.
- Executar avaliações automatizadas.
- Permitir que o harness proponha novos experimentos.
- Registrar configurações, resultados e histórico para reprodutibilidade.
- Definir perfis de instrução (científico, jurídico, corporativo, engenheiro de software, etc.).
- Montar conjuntos de instrução por perfil de forma implícita (fluxo de montagem de perfil).
- Armazenar treinamentos, padrões e personalizações.
- Exibir estatísticas no painel administrativo.
- Testar modelos treinados diretamente no chat web UI.

## 4. Estrutura-alvo da solução

A estrutura abaixo representa a arquitetura desejada. A organização real deverá ser adaptada ao repositório existente após uma auditoria.

```
Rochas.AI.Manager/
  Rochas.AI.Manager.sln
  src/
    Rochas.AI.Manager.Web/
      Controllers/
      Views/
        Shared/
        Chat/
        Dashboard/
        Training/
        Datasets/
        Experiments/
        Profiles/
        Settings/
      wwwroot/
        css/
        js/
        lib/
      Program.cs
    Rochas.AI.Manager.Domain/
      Common/
      Training/
      Datasets/
      Experiments/
      Models/
      Profiles/
    Rochas.AI.Manager.Application/
      Abstractions/
      Training/
        Commands/
        Queries/
      Services/
        Hardware/
        Datasets/
        Experiments/
        Profiles/
        Inference/
    Rochas.AI.Manager.Infrastructure/
      Persistence/
        Context/
        Configurations/
        Repositories/
        UnitOfWork/
      OpenCodeBridge/
      Hardware/
      ModelStorage/
      InferenceServers/
    Rochas.AI.Manager.TrainingEngine/
      Checkpoints/
      Tokenization/
      Models/
        Gemma4/
        Llama/
        Qwen/
        DeepSeek/
      TensorOps/
      Adapters/
        LoRA/
      Optimization/
      Serialization/
    Rochas.AI.Manager.TrainingWorker/
      Jobs/
      Scheduling/
      Monitoring/
  tests/
    Rochas.AI.Manager.Domain.Tests/
    Rochas.AI.Manager.Application.Tests/
    Rochas.AI.Manager.Infrastructure.Tests/
    Rochas.AI.Manager.TrainingEngine.Tests/
```

Não se deve duplicar projetos ou diretórios que já existam na base Gemma Forge. A implementação deverá primeiro identificar o que pode ser reutilizado e o que realmente precisa ser criado. Os novos diretórios (Profiles, InferenceServers, Models/Llama, Models/Qwen, Models/DeepSeek) refletem o suporte a múltiplos modelos e perfis de instrução.

## 5. Arquitetura em camadas

### Domain

Contém as entidades, agregados, objetos de valor, regras de negócio e transições de estado. Não deve depender de MVC, Entity Framework Core, TorchSharp ou da bridge.

### Application

Contém os casos de uso, Commands, Queries, DTOs, validações e interfaces para os serviços externos. É responsável por coordenar as operações de negócio sem implementar diretamente o acesso ao banco ou as operações de GPU.

### Infrastructure

Implementa persistência, repositórios, Unit of Work, armazenamento de arquivos, detecção de hardware, integração com a bridge e integração com servidores de inferência (Ollama, llama turboquant).

### Web

Contém controllers MVC, Razor Views, filtros, autenticação, autorização e endpoints HTTP. Os controllers devem ser finos e delegar a lógica para a camada Application.

### TrainingEngine

Contém o motor numérico C#, a arquitetura dos modelos suportados, o carregamento dos pesos, o cálculo de loss, autograd, LoRA, otimização e serialização dos checkpoints. Não deve depender dos controllers, do banco de dados ou dos repositórios.

### TrainingWorker

Executa os jobs em segundo plano, controla o acesso à GPU, monitora os processos de treinamento e persiste os resultados através das interfaces da aplicação. O worker deve utilizar escopos de dependência próprios para serviços que dependam de DbContext.

## 6. DDD, Generic Repository e Unit of Work

O projeto utilizará repositório genérico para operações comuns de persistência e repositórios especializados para consultas específicas dos agregados.

**Contrato conceitual:**

```
IRepository<TEntity, TId>
  GetByIdAsync
  AddAsync
  Update
  Remove
```

**Contrato do Unit of Work:**

```
IUnitOfWork
  SaveChangesAsync
```

A implementação utilizará **Rochas.DapperRepository** (ORM completa baseada em Dapper) no lugar do Entity Framework Core, com conexões e comandos Dapper, mantendo o contrato de Unit of Work.

Os repositórios especializados previstos são: ITrainingJobRepository, IDatasetRepository, IExperimentRepository, IModelArtifactRepository, IInstructionProfileRepository, ITrainingPatternRepository.

O repositório genérico não deverá substituir as regras de negócio dos agregados.

Os principais agregados serão:

- **TrainingJob** — responsável pelo estado do job, configuração aprovada, início, cancelamento, falhas e conclusão.
- **Dataset** — responsável pela identidade, versão, formato, validação e integridade dos dados.
- **Experiment** — responsável pela hipótese, configuração, execução, métricas e comparação dos resultados.
- **ModelArtifact** — responsável pela referência aos checkpoints, hashes, formatos, metadados e estado de aprovação.
- **TrainingProfile** — responsável pelas políticas de hardware, métodos de treinamento, parâmetros permitidos e limites de execução.
- **InstructionProfile** — responsável pelo conjunto de instruções de um perfil (científico, jurídico, corporativo, engenheiro de software, etc.), incluindo templates e padrões implícitos.
- **TrainingPattern** — responsável pelos padrões reutilizáveis de treinamento (instruções-base, formatos, hiperparâmetros sugeridos) associados a um perfil e a um modelo.

Os pesos do modelo não devem ser armazenados no banco relacional. O banco deverá guardar metadados, estados, métricas, perfis, padrões e referências aos arquivos.

## 7. Interface ASP.NET Core MVC

A interface deverá reutilizar o layout existente do chat web. A implementação deverá preservar: navegação lateral, cabeçalho, identidade visual, CSS existente, layout responsivo, comportamento atual do chat e funcionalidades existentes da bridge. Os novos menus serão adicionados ao layout existente.

**Menus propostos:** Chat e OpenCode, Dashboard, Modelos, Datasets, Perfis de instrução, Configuração de tuning (básico/avançado), Treinamentos, Experimentos, Checkpoints, Avaliações, Estatísticas (admin), Configurações.

**Dashboard** — exibirá: GPU detectada, VRAM total e disponível, estado do worker, jobs ativos, últimos experimentos, métricas recentes, checkpoints disponíveis, alertas de incompatibilidade, resumo de perfis e padrões cadastrados.

**Modelos** — permitir visualizar checkpoints, variantes, formatos, arquitetura e compatibilidade conhecida, para Gemma 4, Llama, Qwen e DeepSeek.

**Datasets** — permitir importar JSONL e outros formatos suportados, visualizar exemplos, validar templates e registrar versões.

**Perfis de instrução** — permitir criar, editar e versionar perfis (científico, jurídico, corporativo, engenheiro de software, etc.), associando conjuntos de instrução e padrões. O fluxo de montagem de perfil carrega os padrões implicitamente.

**Configuração de tuning** — permitir configurar método, contexto, batch size, acumulação de gradientes, learning rate, épocas, quantização e parâmetros LoRA. Em modo básico, apenas escolhas pragmáticas são expostas; em modo avançado, todos os parâmetros.

**Treinamentos** — permitir acompanhar progresso, loss, consumo de VRAM, logs, estado do job e cancelamento.

**Experimentos** — permitir comparar versões, consultar métricas e visualizar os critérios utilizados para aprovar ou rejeitar um modelo.

**Estatísticas (admin)** — painel com métricas agregadas de uso, jobs, modelos, perfis e resultados (ver seção 26).

**Interação multimídia e áudio** — a interface web deverá evoluir para suportar captura de áudio pelo microfone no cliente, envio ao backend para conversão e interpretação por modelos multimodais (ex.: Gemma 4), além de reprodução de texto como áudio via ChatTTS. Funcionalidades: captura de áudio no navegador via Web Audio API / MediaRecorder; envio do áudio ao backend para transcrição/conversão; interpretação multimodal por modelos compatíveis (Gemma 4, etc.); reprodução de respostas como áudio via integração com ChatTTS; suporte futuro a app mobile com as mesmas capacidades.

**Testes no chat web UI** — permitir testar modelos treinados diretamente no chat web existente, selecionando perfil e modelo, sem sair da interface (ver seção 27).

## 8. AJAX puro

A interface utilizará JavaScript nativo e fetch para chamadas HTTP. Não utilizar Blazor, React, Vue, Axios ou jQuery como requisito da nova interface. O frontend deverá: enviar dados para os controllers MVC; receber respostas JSON; apresentar erros de validação; atualizar o estado dos jobs; consultar métricas e logs; enviar comandos de cancelamento; atualizar elementos da página sem recarregar todo o documento; atualizar estatísticas e resultados de teste no chat sem reload completo. Os formulários deverão utilizar proteção antifalsificação, incluindo tokens CSRF quando aplicável. O backend deverá validar as entradas e aplicar autenticação e autorização. Para atualização de progresso, poderá ser utilizado polling AJAX inicialmente. SignalR poderá ser incorporado quando necessário para comunicação em tempo real.

## 9. Controllers previstos

HomeController, ChatController, DashboardController, ModelController, DatasetController, ProfileController, TrainingController, ExperimentController, EvaluationController, StatisticsController, SettingsController. Os controllers deverão chamar serviços da camada Application. Não deverão: executar treinamento diretamente; instanciar modelos TorchSharp; acessar diretamente a GPU; implementar regras de negócio; manipular arquivos de checkpoint sem passar pelos serviços apropriados.

## 10. API e operações de treinamento

Endpoints conceituais:

```
GET  /api/hardware
GET  /api/models
POST /api/datasets
GET  /api/datasets/{id}
POST /api/datasets/{id}/validate
GET  /api/profiles
POST /api/profiles
POST /api/profiles/{id}/assemble
POST /api/training/estimate
POST /api/training/jobs
GET  /api/training/jobs
GET  /api/training/jobs/{id}
POST /api/training/jobs/{id}/cancel
POST /api/experiments/{id}/evaluate
GET  /api/experiments/{id}/metrics
GET  /api/statistics
POST /api/chat/test
GET  /api/artifacts/{id}
```

As rotas definitivas deverão respeitar o padrão atual do projeto. Um pedido para criar treinamento deverá ser validado, persistido e enfileirado. A resposta HTTP 202 Accepted indicará que o job foi aceito para processamento, não que o treinamento terminou.

## 11. Execução em segundo plano

O treinamento nunca deverá ser executado dentro de uma requisição HTTP. Fluxo esperado: o usuário configura um experimento (ou escolhe um perfil); a camada Application valida os parâmetros; o domínio cria o TrainingJob; o Unit of Work persiste o estado inicial; o worker seleciona o job; o hardware é verificado novamente; o motor TorchSharp executa o treinamento; métricas e logs são registrados; checkpoints são salvos; o resultado é avaliado; o job é marcado como concluído, falho ou cancelado. A fila deverá ser persistente. Inicialmente, poderá utilizar o banco de dados, com controle de concorrência e recuperação de jobs interrompidos. O padrão Outbox poderá ser utilizado para garantir consistência entre a persistência do job e sua publicação na fila. O worker deverá controlar a exclusividade de acesso à GPU, oferecer cancelamento cooperativo e evitar processos órfãos.

## 12. Treinamento em C# com TorchSharp (multi-modelo)

O produto final não deverá depender de scripts ou processos Python. TorchSharp será a base numérica, fornecendo operações tensoriais, autograd, módulos neurais e integração com LibTorch. Entretanto, TorchSharp não fornece automaticamente um motor de fine-tuning pronto para Gemma 4 12B nem para os demais modelos suportados. Será necessário implementar e validar os componentes específicos da arquitetura.

**Componentes previstos:** CheckpointReader (Safetensors); Gemma4Config / LlamaConfig / QwenConfig / DeepSeekConfig; tokenizers por modelo; Gemma4TextModel / LlamaTextModel / QwenTextModel / DeepSeekTextModel; atenção híbrida / RoPE / normalizações / MLP / projeções; TrainingBatch; CausalLanguageModelLoss; LoRAAdapter; AdamWOptimizer ou integração compatível; LearningRateScheduler; TrainingLoop; CheckpointWriter; TrainingEvaluator; InferenceClient (Ollama / llama turboquant).

**Carregamento de checkpoints:** o loader deverá ler metadados do checkpoint; interpretar os arquivos Safetensors; validar nomes e dimensões dos tensores; mapear os pesos para os módulos correspondentes; rejeitar arquiteturas incompatíveis; verificar integridade dos arquivos; evitar carregamento de arquivos arbitrários ou não confiáveis. Um modelo GGUF preparado para inferência não deverá ser considerado automaticamente treinável.

**Arquitetura textual:** a implementação textual será o primeiro objetivo para cada modelo suportado. O suporte multimodal deverá ser desenvolvido posteriormente, pois envolve os caminhos de imagem e áudio, pré-processamento e operações específicas. Para manipulação de imagens e documentos PDF, a plataforma utilizará componentes próprios: **Rochas.ImageGenerator** (leitura de arquivos em base64 e geração de imagens) e **Rochas.PDFGenerator** (wrapper baseado em QuestPDF).

## 13. Estratégia de fine-tuning

O primeiro método será LoRA, por ser mais viável que o treinamento completo de todos os pesos. A implementação deverá: congelar os pesos-base; inserir adaptadores nos módulos selecionados; treinar apenas os parâmetros dos adaptadores; calcular gradientes válidos; aplicar atualizações do otimizador; salvar os adaptadores; permitir retomar treinamentos; validar os resultados. A quantização utilizada para treinamento, incluindo QLoRA, somente deverá ser adicionada após confirmar que as operações quantizadas e os gradientes necessários estão corretamente suportados. A seleção automática de treinamento deverá levar em consideração a variante do modelo, VRAM, RAM, contexto, precisão, batch size e método de treinamento. Em modo básico, o usuário escolhe o modelo e o perfil; os hiperparâmetros são sugeridos automaticamente. Em modo avançado, o usuário pode ajustar todos os parâmetros.

## 14. Seleção automática de hardware

O serviço de hardware deverá detectar: modelo da GPU, número de GPUs, VRAM total, VRAM livre, driver NVIDIA, disponibilidade de CUDA, RAM, espaço em disco, compatibilidade com TorchSharp e LibTorch. A estimativa de memória deverá considerar pesos, ativações, gradientes, buffers, estados do otimizador e contexto. O sistema poderá oferecer os perfis: Conservative, Balanced, Performance. Nenhuma configuração deverá ser considerada viável apenas pelo nome da GPU ou por uma estimativa aproximada. Antes de iniciar o treinamento, o sistema deverá executar um preflight e exigir confirmação de acordo com a política configurada.

## 15. Harness autônomo

O harness deverá atuar como planejador e analista dos experimentos. Poderá: propor planos de treinamento; recomendar hiperparâmetros; identificar problemas nos datasets; analisar métricas; sugerir novos experimentos; comparar versões do modelo; solicitar avaliações; sugerir perfis de instrução adequados ao caso de uso. Todas as operações deverão passar pelos serviços da aplicação. O harness não poderá alterar diretamente o banco, modificar pesos arbitrariamente ou executar comandos irrestritos. As configurações propostas deverão ser validadas por regras determinísticas antes da execução. A promoção de um modelo deverá exigir critérios objetivos de avaliação.

## 16. Integração com a bridge OpenCode

A bridge e o chat web atuais deverão ser preservados. A camada Infrastructure poderá encapsular os serviços existentes em interfaces, permitindo que o restante da aplicação utilize a bridge sem depender de detalhes de implementação. A integração deverá permitir que o harness utilize o contexto do projeto para sugerir experimentos, sem misturar o fluxo de conversação com o ciclo de treinamento. Não deverá ser criado um segundo chat ou uma segunda bridge sem necessidade comprovada.

## 17. Testes e critérios de aceitação

**Infraestrutura numérica:** CUDA está disponível; operações tensoriais funcionam na GPU; autograd calcula gradientes finitos; o otimizador atualiza os parâmetros; a loss diminui em um teste sintético.

**Arquitetura do modelo:** o checkpoint é carregado; as dimensões dos tensores são validadas; os logits são comparados com uma referência confiável; a loss causal é validada; os gradientes são verificados.

**LoRA:** os pesos-base permanecem congelados; apenas os parâmetros autorizados são atualizados; os adaptadores são salvos e restaurados; a execução pode ser retomada.

**Múltiplos modelos:** checkpoints de Gemma 4, Llama, Qwen e DeepSeek são carregados corretamente; inferência via Ollama e llama turboquant servers funciona.

**Perfis e modo básico/avançado:** perfis de instrução são criados, versionados e aplicados; fluxo de montagem de perfil carrega padrões implicitamente; modo básico oculta parâmetros granulares; modo avançado os expõe.

**Aplicação:** o chat existente continua funcionando; a bridge permanece operacional; os jobs são persistidos; o cancelamento funciona; os estados são recuperados após reinicialização; a interface atualiza métricas sem recarregar toda a página; estatísticas no admin são exibidas corretamente; testes no chat web UI funcionam para modelos treinados; os resultados são reproduzíveis a partir das configurações registradas.

## 18. Plano de implementação

**Fase 1 — Auditoria da base:** analisar a solução Gemma Forge; mapear o chat, a bridge e os pontos de entrada; identificar o ORM, repositórios e Unit of Work existentes; mapear o frontend atual; executar os testes existentes; registrar as dependências e compatibilidades. Entrega: mapa de integração e plano de alterações sem duplicação de infraestrutura.

**Fase 2 — Estrutura DDD:** criar ou adaptar Domain (incluindo InstructionProfile e TrainingPattern); criar ou adaptar Application; criar ou adaptar Infrastructure (incluindo InferenceServers); implementar os agregados e suas regras; configurar Generic Repository e Unit of Work; criar as migrações necessárias. Entrega: persistência dos jobs, metadados, perfis e padrões.

**Fase 3 — TorchSharp e CUDA:** selecionar versões compatíveis; validar a instalação; implementar teste de forward, backward e otimização; medir uso de memória; criar testes automatizados. Entrega: treinamento sintético real na GPU.

**Fase 4 — Motor multi-modelo:** implementar o loader (Safetensors); validar a configuração de cada modelo; implementar a arquitetura textual (Gemma 4, Llama, Qwen, DeepSeek); comparar os logits; validar loss e gradientes; implementar LoRA; integrar clientes de inferência (Ollama, llama turboquant). Entrega: prova técnica de fine-tuning em C! para múltiplos modelos.

**Fase 5 — Worker:** criar fila persistente; implementar execução de jobs; adicionar cancelamento; registrar métricas; implementar recuperação de execução; gerenciar acesso exclusivo à GPU. Entrega: execução de treinamento em segundo plano.

**Fase 6 — Interface MVC:** reaproveitar o layout existente; adicionar menus (perfis, estatísticas, testes no chat); implementar as páginas de hardware, datasets, perfis e tuning (básico/avançado); criar o monitoramento; adicionar avaliação, gerenciamento de checkpoints e estatísticas; implementar testes no chat web UI. Entrega: plataforma funcional acessível pelo navegador.

**Fase 7 — Automação:** integrar o harness aos casos de uso; permitir propostas de experimentos e perfis; aplicar políticas e limites; automatizar avaliações; registrar evidências e histórico. Entrega: ciclo de experimentação controlado e auditável.

## 19. Limitações e riscos técnicos

A existência de TorchSharp não significa que a arquitetura dos modelos suportados já esteja implementada para treinamento em .NET. O trabalho de desenvolver decoders compatíveis, carregar os pesos, implementar atenção híbrida, validar gradientes e construir LoRA exige engenharia própria. O custo de memória é elevado. Os pesos do modelo, sozinhos, podem ocupar dezenas de gigabytes dependendo da precisão. Ativações, gradientes e estados do otimizador aumentam o consumo. A arquitetura multimodal é mais complexa que a textual e deverá ficar fora do primeiro marco. O projeto deve validar a compatibilidade do checkpoint, da GPU e dos servidores de inferência antes de prometer suporte completo. Não se deve afirmar que o treinamento está funcional até que o forward, backward, cálculo de loss e atualização dos parâmetros tenham sido demonstrados e comparados com uma referência confiável.

## 20. Próximo passo

O primeiro passo prático é disponibilizar a base funcional do Gemma Forge, preferencialmente por meio do repositório ou de um arquivo ZIP. A auditoria deverá identificar: estrutura da solução; linguagens e runtimes utilizados atualmente; ponto de entrada da aplicação web; implementação da bridge; implementação do chat; ORM e banco utilizados; repositórios e Unit of Work; estrutura dos arquivos estáticos e Razor Views; testes existentes; pontos de extensão apropriados. A partir dessa análise, as alterações deverão ser planejadas arquivo por arquivo, preservando o funcionamento atual. A prioridade técnica inicial será validar TorchSharp com CUDA e construir uma prova de treinamento sintético. Depois, o desenvolvimento avançará para o carregamento do checkpoint e a implementação textual dos modelos suportados.

## 21. Suporte a múltiplos modelos e servidores de inferência

A plataforma deverá suportar, além do Gemma 4, modelos como Llama, Qwen e DeepSeek, utilizando como base de inferência servidores locais como Ollama e llama turboquant servers. A camada de inferência deverá: encapsular clientes para Ollama e llama turboquant servers em interfaces; permitir selecionar o modelo e o servidor de inferência por job/experimento; validar disponibilidade e compatibilidade do modelo no servidor; reutilizar a bridge OpenCode existente para encaminhamento de conteúdo; não criar um segundo chat ou uma segunda bridge sem necessidade comprovada. Os pesos de treinamento permanecem no motor próprio (TorchSharp). Os servidores de inferência são utilizados para execução e teste dos modelos, não para o treinamento em si.

## 22. Perfis de instrução (conjuntos por perfil)

Os perfis de instrução são conjuntos predefinidos de instruções, templates e padrões associados a um domínio de uso. Exemplos: científico, jurídico, corporativo, engenheiro de software, entre outros. Cada perfil deverá conter: nome e descrição; conjunto de instruções-base (system prompts, templates de conversa); padrões de treinamento associados (hiperparâmetros sugeridos, formato de dataset); versão e histórico de alterações; estado de aprovação. Os perfis são versionados e reutilizáveis, permitindo que a organização acumule conhecimento e padrões ao longo do tempo.

## 23. Modo básico vs modo avançado

Para tornar a plataforma acessível a usuários comuns, o treinamento é oferecido em dois modos:

**Modo básico (pragmático):** o usuário escolhe o modelo e o perfil de instrução; o sistema sugere automaticamente datasets, hiperparâmetros e padrões; não expõe pesos, gradientes ou configurações granulares; ideal para personalização e uso doméstico.

**Modo avançado (granular):** expõe todos os parâmetros: learning rate, batch size, acumulação de gradientes, LoRA, quantização, etc.; permite ajuste fino por usuários experientes; mantém validações determinísticas e preflight de hardware.

A alternância entre modos deve ser simples e não deve quebrar a consistência dos jobs em andamento.

## 24. Fluxo de montagem de perfil

O fluxo de montagem de perfil permite criar um perfil de instrução carregando implicitamente os padrões adequados, sem que o usuário precise configurar manualmente cada detalhe. Etapas do fluxo: selecionar o domínio/perfil desejado (científico, jurídico, corporativo, engenheiro de software, etc.); o sistema carrega os padrões de instrução associados ao perfil (templates, system prompts, hiperparâmetros sugeridos); o usuário revisa e ajusta (opcional) os itens sugeridos; o perfil é salvo e versionado; o perfil fica disponível para uso em treinamentos e testes no chat web UI. Esse fluxo reduz a carga cognitiva do usuário e garante consistência entre treinamentos do mesmo perfil.

## 25. Armazenamento de treinamentos e padrões

A plataforma deverá armazenar treinamentos, padrões e personalizações de forma organizada: treinamentos (jobs, métricas, logs, checkpoints e adaptadores LoRA); padrões (conjuntos de instrução reutilizáveis associados a perfis e modelos); personalizações (ajustes feitos pelo usuário sobre padrões-base); versionamento de perfis e padrões para reprodutibilidade; referências aos arquivos (pesos não são armazenados no banco relacional). O armazenamento deve permitir auditoria e recuperação de qualquer treinamento ou perfil a partir das configurações registradas.

## 26. Estatísticas no admin

O painel administrativo deverá exibir estatísticas agregadas para apoio à decisão: quantidade de jobs (ativos, concluídos, falhos, cancelados); uso por modelo e por perfil de instrução; métricas recentes (loss, VRAM, tempo de treinamento); checkpoints e adaptadores disponíveis; histórico de experimentos e comparações; alertas de incompatibilidade de hardware. As estatísticas são atualizadas via AJAX (polling) ou SignalR, sem recarregar toda a página.

## 27. Testes no chat web UI

Após o treinamento, o usuário deverá poder testar o modelo diretamente no chat web existente, sem sair da interface. Funcionalidades: selecionar modelo treinado e perfil de instrução; enviar prompts e visualizar respostas em tempo real; comparar respostas do modelo-base vs modelo ajustado; registrar evidências de teste para auditoria; utilizar a bridge OpenCode existente para encaminhamento quando aplicável. Os testes no chat web UI fecham o ciclo de experimentação: treinar, avaliar e testar em um único fluxo.

## 28. Diretriz de evolução da base OpenCodeBridge/Gemma Forge para Rochas.AI.Manager

O desenvolvimento do Rochas.AI.Manager deverá considerar a jornada técnica já realizada no repositório OpenCodeBridge (https://github.com/rocha83/opencodebridge) e a base funcional do Gemma Forge como fontes de implementação, decisões arquiteturais, evidências experimentais e conhecimento acumulado. O objetivo não é iniciar um projeto paralelo, reconstruir funcionalidades já existentes ou descartar as soluções desenvolvidas durante os experimentos com modelos locais. O objetivo é consolidar essa evolução em uma plataforma organizada, testável, extensível e sustentável, preservando o comportamento funcional existente e adaptando sua estrutura ao documento de arquitetura do Rochas.AI.Manager.

### 28.1. Princípio de continuidade e aproveitamento da jornada existente

Antes de qualquer alteração, deverá ser realizada uma auditoria conjunta do código-fonte, dos arquivos Markdown de configuração e setup, dos READMEs, dos históricos cronológicos, dos relatórios de sprint, dos testes automatizados e dos scripts operacionais disponíveis.

A auditoria deverá distinguir explicitamente:

- Funcionalidades implementadas e verificadas por testes.
- Funcionalidades implementadas, mas ainda sem validação suficiente.
- Protótipos e experimentos operacionais.
- Decisões técnicas documentadas que ainda não foram incorporadas ao código.
- Problemas conhecidos, limitações, incompatibilidades e trabalhos pendentes.
- Requisitos futuros do Rochas.AI.Manager que ainda não possuem implementação.

Nenhuma funcionalidade deverá ser considerada concluída exclusivamente porque aparece no README, em um relatório de sprint ou em uma configuração de exemplo. O estado real deverá ser confirmado por inspeção do código, execução dos testes disponíveis e, quando aplicável, reprodução dos experimentos.

### 28.2. Auditoria documental e reconstrução cronológica da jornada

A documentação existente deverá ser tratada como um registro técnico da evolução do laboratório de IA local, e não apenas como material auxiliar de instalação.

Deverão ser localizados e analisados, quando existentes:

- README principal e READMEs específicos de projetos e subsistemas.
- Arquivos de setup do ambiente de desenvolvimento e do ambiente GPU.
- Documentação de instalação, configuração e execução do llama.cpp, llama-server e servidores associados.
- Registros da instalação de drivers NVIDIA, CUDA, bibliotecas nativas e dependências de inferência.
- Documentação de quantização, formatos de checkpoint, parâmetros de contexto, cache KV e otimizações de memória.
- Histórico completo dos experimentos com modelos locais, incluindo Qwen, Gemma e outros modelos efetivamente testados.
- Relatórios de benchmark, consumo de VRAM, tokens por segundo, concorrência entre instâncias e limitações encontradas.
- Histórico da implementação da bridge de compatibilidade com OpenCode.
- Documentação do chat web, do harness de agentes, dos perfis de execução e das ferramentas disponíveis aos modelos.
- Relatórios de sprint, testes de integração, resultados de missões e pendências conhecidas.
- Scripts de inicialização, diagnóstico, validação, benchmark e recuperação operacional.

A análise deverá reconstruir a sequência de decisões, indicando o problema original, a hipótese adotada, a alteração implementada, o resultado observado e a consequência para a arquitetura. Quando houver divergência entre documentos, deverá ser identificada a configuração vigente e preservado o histórico da transição. Valores de VRAM, desempenho, contexto e versões deverão permanecer vinculados à configuração em que foram medidos. O resultado deverá incluir um inventário documental e uma linha do tempo técnica que permita compreender como a infraestrutura evoluiu, quais limitações foram superadas e quais permanecem abertas.

### 28.3. Auditoria da infraestrutura local de inferência

A infraestrutura de inferência desenvolvida durante a jornada deverá ser preservada como subsistema operacional próprio, independente do futuro motor de treinamento TorchSharp.

Deverão ser identificados os componentes responsáveis por:

- Descoberta dos modelos disponíveis e localização dos arquivos de pesos.
- Inicialização e encerramento dos servidores de inferência.
- Configuração de contexto, quantização, paralelismo e utilização da GPU.
- Distribuição de modelos entre instâncias e controle de concorrência.
- Verificação de saúde dos servidores e disponibilidade das APIs.
- Medição de VRAM, memória RAM, tempo de resposta e velocidade de geração.
- Identificação de erros de carregamento, esgotamento de memória e falhas de execução.
- Registro dos parâmetros efetivamente utilizados em cada experimento.

A camada de infraestrutura do Rochas.AI.Manager deverá encapsular esses recursos por meio de interfaces de aplicação, permitindo que o restante da plataforma opere sem depender diretamente dos detalhes de inicialização do llama.cpp, Ollama ou de outros servidores compatíveis. As configurações específicas de cada máquina deverão permanecer externas ao domínio de negócio. Caminhos locais, portas, argumentos de inicialização, versões de binários, limites de memória e parâmetros operacionais deverão ser registrados em configurações versionáveis, sem incluir credenciais ou segredos no repositório. O sistema deverá distinguir capacidade de inferência de capacidade de treinamento. Um modelo carregado com sucesso em formato GGUF, mesmo com quantização e otimizações avançadas, não será considerado automaticamente compatível com o carregamento de pesos, cálculo de gradientes ou treinamento via TorchSharp.

### 28.4. Preservação e evolução da bridge OpenCode

A bridge existente é um componente central do Rochas.AI.Manager e deverá continuar sendo a camada de compatibilidade entre o cliente OpenCode e os servidores de inferência locais, respeitando o contrato efetivamente implementado e validado. A implementação atual deverá ser auditada para identificar os caminhos de requisição e resposta, as conversões entre formatos de API, o tratamento de mensagens, os mecanismos de streaming, o raciocínio explícito dos modelos, as chamadas de ferramentas, os erros, os cancelamentos e os demais comportamentos já suportados.

A compatibilidade com Responses API e Chat Completions, quando existente, deverá ser preservada por testes de contrato que verifiquem as transformações de entrada e saída. Não deverá ser presumido que as duas APIs são semanticamente equivalentes em todos os seus recursos. A bridge não deverá incorporar regras de negócio relacionadas a treinamento, datasets, experimentos ou persistência de checkpoints. Sua responsabilidade continuará concentrada na compatibilidade de protocolo, no encaminhamento de requisições e na tradução necessária para a execução dos modelos.

A integração com a arquitetura DDD deverá ser feita por adaptadores e interfaces, sem reescrever desnecessariamente o componente existente. Se a bridge já possuir uma organização interna adequada, ela deverá ser mantida e encapsulada gradualmente.

Qualquer alteração deverá preservar, conforme aplicável, os seguintes comportamentos:

- Comunicação entre OpenCode e os modelos locais.
- Formato das requisições e respostas já aceitas.
- Streaming e encerramento correto das respostas.
- Preservação ou conversão controlada dos campos de raciocínio.
- Chamadas de ferramentas e seus resultados.
- Tratamento de erros e respostas incompletas.
- Configurações específicas de cada modelo.
- Compatibilidade com os testes e cenários existentes.

A seleção de modelos ajustados deverá utilizar a infraestrutura de inferência existente, sempre que o formato e o servidor forem compatíveis. Não deverá ser implementada uma segunda bridge apenas para testar checkpoints ou adaptadores LoRA. Quando uma funcionalidade não puder ser atendida pelo contrato atual, deverá ser proposta uma extensão explícita e retrocompatível, com testes e justificativa técnica.

### 28.5. Preservação e evolução do chat web existente

O chat web atual deverá ser mantido como interface principal de conversação e como ambiente de validação prática dos modelos locais. A auditoria deverá identificar sua tecnologia real, os componentes de interface, o mecanismo de comunicação com o backend, o fluxo de streaming, o gerenciamento de conversas, a seleção de modelos, os recursos de interação com ferramentas e as dependências de frontend.

A migração para ASP.NET Core MVC com Razor Views e JavaScript nativo deverá preservar as funcionalidades comprovadamente existentes, adaptando gradualmente os pontos de integração. Não deverá ser criado um segundo chat independente apenas para atender às novas telas administrativas. As funcionalidades de gerenciamento de modelos, treinamento e avaliação deverão ser acrescentadas à navegação existente, respeitando a identidade visual, o layout responsivo e os contratos atuais.

O chat deverá evoluir para permitir, quando os componentes necessários estiverem disponíveis:

- Selecionar o modelo-base ou um artefato treinado.
- Selecionar um perfil de instrução versionado.
- Aplicar os padrões associados ao perfil sem exigir configuração manual de cada item.
- Comparar respostas de um modelo-base com as de uma variante ajustada.
- Consultar a identificação do modelo, checkpoint, adaptador e configuração de inferência utilizados.
- Registrar execuções de avaliação e evidências associadas aos experimentos.
- Encaminhar as conversas pela bridge existente quando aplicável.

O teste de um modelo treinado não deverá ser considerado válido apenas porque a interface conseguiu selecionar um artefato. A camada de inferência deverá confirmar que o servidor suporta o formato e a combinação de pesos ou adaptadores selecionados.

### 28.6. Evolução do harness de agentes para um supervisor de experimentos

O harness existente deverá ser auditado como candidato a núcleo de automação do Rochas.AI.Manager. Seu comportamento atual, seus prompts, seus perfis por modelo, sua divisão de responsabilidades, suas ferramentas, seus mecanismos de contenção de repetição e seus critérios de conclusão deverão ser preservados quando já estiverem funcionando.

A evolução deverá separar claramente dois domínios de operação:

- **Harness de desenvolvimento:** auxilia a implementação do próprio Rochas.AI.Manager, analisa o código, propõe alterações, executa verificações autorizadas e registra evidências.
- **Harness de experimentação:** planeja experimentos de treinamento, recomenda configurações, analisa datasets e métricas, propõe novas avaliações e sugere a promoção ou rejeição de artefatos.

Os dois domínios poderão compartilhar componentes técnicos, clientes de inferência e mecanismos de avaliação, mas não deverão compartilhar indiscriminadamente permissões, filas ou estados de execução. O harness de experimentação deverá trabalhar por meio de casos de uso da camada Application. Não poderá acessar diretamente o banco, alterar estados de agregados sem validação, modificar pesos arbitrariamente ou executar comandos irrestritos.

Toda proposta de experimento deverá ser convertida em uma configuração explícita, validada por regras determinísticas e submetida ao preflight de hardware antes de ser enfileirada. O harness deverá considerar os resultados históricos para sugerir configurações mais adequadas, mas as recomendações deverão ser identificadas como hipóteses até que sejam executadas e avaliadas.

As decisões automáticas deverão ser auditáveis, incluindo o modelo utilizado para a análise, a versão do perfil, os dados considerados, os parâmetros propostos, as regras aplicadas, os resultados obtidos e o motivo da decisão final. A promoção de um modelo deverá exigir critérios objetivos definidos previamente. Uma resposta aparentemente boa do harness ou uma redução isolada da loss não deverá ser suficiente para aprovar um artefato.

### 28.7. Separação entre inferência, bridge, harness e treinamento

A arquitetura deverá preservar a separação de responsabilidades entre quatro subsistemas:

- **Inferência:** executa os modelos por meio dos servidores compatíveis e fornece métricas operacionais.
- **Bridge:** traduz protocolos e encaminha conteúdo entre clientes e servidores de inferência.
- **Harness:** planeja, coordena e avalia tarefas e experimentos por meio dos serviços autorizados.
- **TrainingEngine:** executa operações numéricas, forward, backward, cálculo de loss, atualização dos parâmetros e geração de checkpoints compatíveis.

Esses subsistemas poderão compartilhar contratos e serviços de aplicação, mas não deverão depender de detalhes internos uns dos outros além do necessário. A execução de inferência de um modelo-base ou ajustado não será confundida com o treinamento do modelo. Da mesma forma, a capacidade do harness de solicitar uma tarefa não significará que ele pode contornar a validação dos parâmetros ou os controles de hardware. O TrainingWorker será responsável pelo processamento assíncrono dos jobs de treinamento. A bridge e os controllers web não deverão executar diretamente o loop de treinamento nem manter requisições HTTP abertas durante experimentos longos.

### 28.8. Arquitetura de treinamento orientada por evidências

A implementação do motor TorchSharp deverá aproveitar os conhecimentos obtidos durante a execução dos modelos locais, mas manter uma prova de viabilidade independente para treinamento. Os resultados anteriores de inferência, quantização, contexto, cache KV e desempenho servirão como referência para dimensionamento e planejamento, não como prova de que o treinamento é viável.

A evolução do TrainingEngine deverá seguir marcos verificáveis:

- Confirmar a compatibilidade entre .NET, TorchSharp, LibTorch, CUDA e os drivers da máquina.
- Demonstrar operações tensoriais reais na GPU e verificar autograd.
- Executar um teste sintético de forward, backward e otimização, comprovando gradientes finitos e redução de loss em condições controladas.
- Implementar o carregamento seguro de checkpoints de treinamento e a validação das dimensões dos tensores.
- Validar a arquitetura textual de cada modelo contra uma referência confiável.
- Implementar e validar o treinamento de adaptadores LoRA, mantendo os pesos-base congelados.
- Demonstrar persistência, restauração, retomada e avaliação de adaptadores.
- Somente então avançar para experimentos progressivamente maiores, sujeitos aos limites reais de memória e processamento.

O suporte deverá ser declarado individualmente para cada combinação de arquitetura, variante, formato de pesos, precisão e método de treinamento. A existência de suporte a inferência de Gemma, Llama, Qwen ou DeepSeek não implicará suporte automático ao treinamento dessas famílias. O primeiro marco deverá priorizar uma prova sintética e reproduzível na GPU. A execução de fine-tuning real de um modelo grande será um marco posterior e dependerá da validação de memória, compatibilidade numérica e fidelidade dos resultados.

### 28.9. Catálogo de modelos e configurações reproduzíveis

O Rochas.AI.Manager deverá transformar o histórico de experimentos em um catálogo estruturado de modelos, configurações e evidências.

Para cada execução relevante, deverão ser registrados, quando disponíveis:

- Família, variante, identificador e revisão do modelo.
- Formato dos pesos e quantização.
- Origem e hash dos arquivos.
- Servidor de inferência e versão do runtime.
- Parâmetros de inicialização e configuração de contexto.
- Configuração de GPU, CUDA, driver e bibliotecas relevantes.
- Consumo de VRAM e RAM, velocidade de geração e duração.
- Configuração do harness, perfil de instrução e ferramentas habilitadas.
- Dataset, versão, parâmetros de treinamento e identificador do experimento.
- Métricas, logs, erros, artefatos produzidos e critérios de aprovação.
- Estado de reprodutibilidade e limitações conhecidas.

Os dados deverão ser persistidos como metadados e referências a arquivos, sem armazenar pesos volumosos no banco relacional. Os arquivos de modelos, datasets, checkpoints e adaptadores deverão ser mantidos em armazenamento apropriado, com identificação, integridade, política de acesso e controle de versões. Configurações antigas não deverão ser silenciosamente sobrescritas. A evolução dos experimentos deverá permitir comparar resultados anteriores e posteriores e reconstruir as condições de execução sempre que os artefatos e as dependências necessárias estiverem disponíveis.

### 28.10. Integração da documentação com a operação da plataforma

A documentação técnica deverá acompanhar a implementação e ser atualizada à medida que a arquitetura evoluir. Os arquivos Markdown de setup deverão permanecer como referência operacional para preparação do ambiente, diagnóstico, execução dos servidores e reprodução dos benchmarks. Quando houver configurações específicas de desenvolvimento, testes ou produção, elas deverão ser identificadas separadamente.

O Rochas.AI.Manager deverá expor informações operacionais relevantes no Dashboard, incluindo:

- Estado dos servidores de inferência.
- Modelos carregados e respectivos formatos.
- Estado da GPU e memória disponível.
- Compatibilidade conhecida entre modelos, servidores e recursos de hardware.
- Estado do worker de treinamento.
- Jobs ativos, interrompidos, concluídos ou falhos.
- Alertas de configuração, memória e dependências.
- Últimos experimentos e seus resultados.
- Versões de configuração utilizadas em execuções anteriores.

O painel não deverá apresentar informações presumidas como medições atuais. Métricas deverão ter origem identificável, horário de coleta e indicação de indisponibilidade quando não puderem ser obtidas. As atualizações deverão utilizar os mecanismos já adotados pela aplicação, preferencialmente polling com AJAX e fetch no primeiro estágio, mantendo a possibilidade de introduzir SignalR quando houver necessidade comprovada.

### 28.11. Estratégia de migração sem ruptura

A evolução deverá ocorrer por etapas pequenas, verificáveis e reversíveis.

Na primeira etapa, deverão ser executados os testes atuais e registrados os comportamentos que precisam permanecer compatíveis. Em seguida, deverão ser identificados os pontos de entrada, contratos e dependências do chat, da bridge, do harness e da infraestrutura de inferência. Na segunda etapa, deverão ser criadas apenas as abstrações necessárias para integração com as camadas Domain, Application e Infrastructure, evitando reescrever componentes estáveis. Na terceira etapa, deverão ser integrados o catálogo de modelos, o diagnóstico de hardware, os perfis de instrução e os metadados de experimentos. Na quarta etapa, deverão ser implementados a fila persistente, o TrainingWorker, o monitoramento de jobs e a prova técnica TorchSharp. Nas etapas posteriores, deverão ser acrescentados o treinamento LoRA, a avaliação automatizada, a integração dos artefatos com o chat existente e a automação controlada pelo harness.

Cada etapa deverá produzir código compilável, testes executáveis, documentação atualizada e critérios de aceitação verificáveis. Não deverão ser realizadas migrações amplas de diretórios, namespaces, tecnologias de frontend ou persistência sem que exista uma justificativa arquitetural e uma estratégia de compatibilidade. Quando for necessário alterar o comportamento da bridge ou do chat, os testes de regressão deverão demonstrar que os fluxos anteriores continuam operacionais.

### 28.12. Entregáveis obrigatórios da auditoria inicial

Antes de iniciar a implementação ampla do Rochas.AI.Manager, deverão ser produzidos os seguintes artefatos:

- Mapa real da solução, com projetos, dependências, pontos de entrada e tecnologias utilizadas.
- Inventário dos arquivos Markdown relevantes, agrupados por setup, arquitetura, histórico, benchmarks e operação.
- Linha do tempo da evolução do ambiente GPU e dos modelos testados.
- Matriz de capacidades dos modelos efetivamente experimentados, separando inferência, uso de ferramentas, comportamento agentivo e treinamento.
- Mapa de integração da bridge, incluindo protocolos, streaming, chamadas de ferramentas, tratamento de erros e testes existentes.
- Mapa do chat web, incluindo layout, navegação, frontend, backend e contratos de comunicação.
- Mapa do harness, com agentes, perfis, políticas, ferramentas, limites e critérios de conclusão.
- Inventário de scripts e procedimentos para instalar, inicializar, diagnosticar e validar o ambiente.
- Lista de componentes reutilizáveis, componentes que exigem adaptação e componentes que precisam ser criados.
- Matriz de riscos, incompatibilidades, pendências e dependências técnicas.
- Plano incremental de implementação alinhado às fases do documento principal.
- Linha de base de testes e benchmarks para verificar regressões e medir os avanços.

Cada conclusão deverá indicar sua origem: código, teste executado, documentação, configuração observada ou hipótese ainda não validada.

### 28.13. Critérios adicionais de aceitação

A integração da jornada OpenCodeBridge/Gemma Forge ao Rochas.AI.Manager será considerada satisfatória quando:

- A aplicação existente puder ser compilada e executada conforme as dependências suportadas.
- O chat web e os fluxos já funcionais permanecerem operacionais.
- A bridge continuar compatível com os cenários de integração cobertos por testes.
- Os experimentos históricos relevantes estiverem documentados com suas configurações e limitações.
- Os servidores de inferência puderem ser diagnosticados e consultados por interfaces definidas.
- Os modelos disponíveis forem identificados sem confundir formatos de inferência e treinamento.
- O harness utilizar os serviços da aplicação e respeitar as políticas de execução.
- Os metadados dos experimentos forem persistidos com referências aos artefatos e às configurações utilizadas.
- O estado dos jobs e as métricas puderem ser consultados pela interface sem recarga completa da página.
- A prova técnica TorchSharp demonstrar operações numéricas válidas na GPU antes de ser declarado qualquer suporte efetivo a fine-tuning.
- As alterações introduzidas tiverem testes de regressão e documentação atualizada.
- Nenhum componente existente for duplicado sem uma justificativa técnica documentada.

### 28.14. Diretriz final

O Rochas.AI.Manager deverá ser construído como a evolução natural da infraestrutura e dos experimentos já realizados, combinando a experiência prática de execução local de modelos, a bridge de compatibilidade com OpenCode, o chat web e o harness com uma arquitetura de aplicação organizada em C#, ASP.NET Core MVC, DDD, Rochas.DapperRepository (Dapper), TorchSharp e processamento assíncrono. O conhecimento acumulado nos arquivos Markdown e no histórico do repositório deverá orientar as decisões de arquitetura, mas não substituir a validação do código e dos resultados. A prioridade é preservar o que funciona, identificar objetivamente o que falta e transformar os experimentos em componentes reutilizáveis, documentados e reproduzíveis. A plataforma somente deverá declarar uma capacidade como concluída quando houver implementação verificável, testes adequados e evidências compatíveis com o nível de suporte anunciado.

O resultado esperado é um ambiente integrado no qual o usuário possa conversar com modelos locais, utilizar o OpenCode por meio da bridge existente, gerenciar modelos e datasets, criar perfis de instrução, planejar experimentos, executar treinamentos controlados, avaliar os resultados e testar os artefatos pelo mesmo chat web, mantendo rastreabilidade desde a configuração do ambiente até a aprovação de cada modelo.

---

## 29. Infraestrutura própria de componentes Rochas.*

A plataforma utilizará componentes próprios, disponíveis no perfil https://github.com/rocha83, em vez de bibliotecas genéricas de terceiros:

- **Rochas.ImageGenerator** — leitura de arquivos em base64 e geração de imagens (manipulação de mídia).
- **Rochas.PDFGenerator** — wrapper baseado em QuestPDF para geração de documentos PDF.
- **Rochas.DapperRepository** — ORM completa baseada em Dapper, no lugar do Entity Framework Core.
- **Rochas.CacheIndexer** — para módulos de bus/cache e indexação.

Esses componentes serão integrados via interfaces da camada Infrastructure, mantendo o domínio desacoplado. A persistência passa a ser Dapper-based, com Unit of Work adaptado ao Rochas.DapperRepository.

## 30. Interação multimídia, áudio e mobile

O módulo web UI existente deverá receber melhorias para interagir com microfone no cliente e capturar áudio para conversão em backend e interpretação por parte dos modelos quando multimodais (ex.: Gemma 4).

- Captura de áudio no navegador via Web Audio API / MediaRecorder.
- Envio do áudio ao backend para transcrição/conversão.
- Interpretação multimodal por modelos compatíveis (Gemma 4, etc.).
- Reprodução de respostas como áudio via integração com ChatTTS.
- Suporte futuro a app mobile com as mesmas capacidades multimídia.
- Manipulação de imagens/documentos via Rochas.ImageGenerator e Rochas.PDFGenerator.

A interação multimídia com a plataforma via web ou, futuramente, app mobile, também deverá ser possível, mantendo a bridge OpenCode e o chat web existentes.

---

**Objetivo final:** transformar a base funcional do Gemma Forge no Rochas.AI.Manager, mantendo o chat e a bridge existentes, com interface ASP.NET Core MVC, AJAX puro, backend C#, arquitetura DDD, Generic Repository, Unit of Work, ORM (Dapper via Rochas.DapperRepository) e um motor de treinamento próprio baseado em TorchSharp (multi-modelo: Gemma 4, Llama, Qwen, DeepSeek), integrado a Ollama/llama turboquant servers, com perfis de instrução, modo básico/avançado, fluxo de montagem de perfil, armazenamento de treinamentos, estatísticas no admin, testes no chat web UI, componentes próprios (Rochas.ImageGenerator, Rochas.PDFGenerator, Rochas.CacheIndexer) e interação multimídia (microfone, áudio, ChatTTS, mobile), sem Python no produto final.

---

## 31. Harness de tools como componente de primeira classe (aprendizados da sessão OpenCodeBridge)

> Seção incremental. Não altera as seções 15, 16, 28.4, 28.6, 28.7, 28.9 e 28.10; complementa-as com o que foi medido na prática ao tentar certificar o harness de tools da bridge.

### 31.1. Dores observadas (evidência, não hipótese)

A sessão de certificação dos belts (azul→preta) expôs problemas concretos que a plataforma deve tratar como requisitos:

| # | Dor | Evidência medida |
|---|---|---|
| D1 | `tool_choice=required` não é honrado de forma confiável pelo llama/Gemma | No harness custom, a faixa "verde" gerou ~41.928 chars de texto e **0 arquivos**; o loop encerrou sem `write` |
| D2 | O cap de saída do operador não era aplicado no repasse `/v1/chat/completions` | Uma única resposta chegou a **40.116 tokens** (~26 min) antes de ser interrompida |
| D3 | `--harness 0` não desligava as caudas de harness | As tails continuavam anexadas (`tools_chars=3368`), misturando harness nosso com o do cliente |
| D4 | Harness custom (loop + nudge + stall + revisão) é caro e instável | Azul PASS, verde FAIL; horas de execução para poucas faixas |
| D5 | Harness nativo do opencode é muito superior para tools | **5/5 PASS em ~12,4 min**, com 4–8 arquivos por faixa |
| D6 | O harness do cliente é uma mistura de nativo + custom | Captura: **14.010 chars**, sendo **43% nativo** (Code Mode, tools, skills, env) e **57% custom** (agente + AGENTS.md) |
| D7 | Hardcodes de infraestrutura vazam para o texto do harness | modelo, porta, contexto (`64k`, `160k/32k`), temperatura, caminho local e "delegar quando >=64k" |
| D8 | Falta de catálogo/reprodutibilidade do harness | Resultados e requests ficavam em `/tmp`, sem versão, hash, diff ou vínculo com o experimento |

### 31.2. Conclusão de arquitetura

O harness não deve ser tratado como texto solto nem como detalhe da bridge. Ele é um **artefato versionado da plataforma**, com três modos explícitos:

- **Cliente nativo:** o cliente (ex.: OpenCode) traz o harness; a bridge apenas transporta. É o modo com melhor taxa de sucesso para tools e deve ser o **default recomendado**.
- **Bridge bypass/legacy:** o cliente não traz harness; a plataforma aplica um perfil sintetizado (núcleo + tails por role).
- **Híbrido:** o cliente traz o harness e a plataforma anexa apenas tails curtas e explícitas (nunca silenciosas).

A bridge permanece **protocol-only** (seção 28.4). Quem decide *qual* perfil aplicar é a camada Application; a bridge apenas recebe um payload/configuração passiva e a repassa.

### 31.3. Harness Profile — modelo de domínio

Novo agregado, versionado e imutável por versão:

- `HarnessProfile`: Id, Name, Description, Status (Draft/Approved/Deprecated), CreatedAt.
- `HarnessProfileVersion`: Version, Mode (ClientNative | BridgeBypass | BridgeLegacy | Hybrid), Checksum, ApprovedBy, ApprovedAt.
- `HarnessAttachment`: Kind (Core | RoleTail | BehaviorTail | ToolsTail | SkillHint), Role (plan/build/orch/exec/scripter/general), Content, CharCount.
- `ToolPolicy`: Role, Allow[], Deny[], RequireCall (bool), MaxTurns, StallLimit.
- `ModelBinding`: Provider, ModelId, MaxTokens, ContextWindow, Thinking, Temperature — **fora do texto do harness**.
- `HarnessRun`: EvidenceId, ProfileVersionId, ModelBindingId, RequestHash, ToolCalls, FilesCreated, ResultCode, DurationMs, TokensPerSecond, CreatedAt.
- `BridgeCapture`: Mode, RequestJson, ResponseJson, RequestHash, CreatedAt (alimentado pela sonda).

Regra de ouro: **harness descreve comportamento; infraestrutura fica na configuração**. Modelo, provider, porta, contexto, temperatura, caminho e limite de contexto nunca entram no texto do prompt.

### 31.4. Admin Web UI — seção "Harness"

A UI administrativa (ASP.NET Core MVC + AJAX, conforme seções 7–8) ganha uma área dedicada:

1. **Lista de perfis e versões** com status, checksum e data; comparação lado a lado.
2. **Editor de núcleo e tails** por role, com contador de caracteres e indicação de limites.
3. **Preview do system prompt final** exatamente como será enviado (merge nativo + custom), com hash.
4. **Diff entre versões** (texto e tamanho), aprovação e publicação.
5. **Seletor de modo**: Cliente nativo / Bridge bypass / Bridge legacy / Híbrido.
6. **Linter de hardcodes em tempo real**: bloqueia ou alerta modelo, porta, contexto, temperatura, caminho e limites numéricos no texto.
7. **Matriz de capacidade por modelo**: suporte a tool calling, thinking, `max_tokens`, contexto e taxa de sucesso observada.
8. **Test Bench (devpipe)**: executa um enunciado canônico por faixa, coleta a evidência (arquivos, rc, tokens/s, duração) e grava o request/response da sonda.
9. **Visualizador da sonda**: request completo, separação automática nativo × custom, botão "extrair harness" e "promover a perfil".
10. **Histórico de runs** por perfil/modelo, com filtros e comparação.

### 31.5. Integração com a bridge (contrato mínimo e retrocompatível)

- Manter `/v1/responses` e `/v1/chat/completions` com os comportamentos atuais (28.4).
- Corrigir e documentar os flags de harness:
  - `--harness 0` desliga de fato as caudas;
  - `--diagnostic 1` habilita a sonda de request/response completo;
  - `--max-tokens` aplicado **também** no repasse, respeitando o valor do cliente quando presente.
- Opcional e retrocompatível: aceitar um identificador de perfil (`X-Harness-Profile`) apenas para *observabilidade*; a composição do texto continua na Application.
- A bridge nunca interpreta regra de negócio, treinamento, dataset ou experimento.

### 31.6. Evidência e reprodutibilidade (liga com 28.9 e 28.10)

Cada execução de harness passa a registrar:

- versão do perfil + checksum do prompt efetivo;
- hash do request enviado ao modelo (system + tools + mensagens);
- modelo, `max_tokens`, temperatura e thinking usados;
- tool calls emitidas, arquivos criados, código de retorno, duração e tokens/s;
- captura completa (sonda) quando habilitada.

Isso permite responder objetivamente: "qual harness, em qual versão, com qual modelo, produziu qual resultado" — e comparar regressões entre experimentos.

### 31.7. Fases de implementação (incrementais e reversíveis)

- **Fase A — Formalizar o que já temos:** criar `HarnessProfile` v1 a partir do `SUMMARY.md` (núcleo + tails, sem hardcodes), persistir a captura da sonda como `BridgeCapture` e expor o Test Bench em modo somente leitura.
- **Fase B — Editar e versionar:** editor, preview, diff, aprovação e publicação no admin; linter de hardcodes bloqueante.
- **Fase C — Tool policy e capacidades:** allow/deny por role, limites de turnos/stall, matriz de capacidade por modelo e geração automática de `agents/*.md` / `opencode.json` a partir do perfil.
- **Fase D — Supervisor de experimentos:** integrar o harness de desenvolvimento e o de experimentação (28.6), mantendo permissões, filas e estados separados.

### 31.8. Critérios de aceitação da seção 31

- Um perfil versionado é aplicado sem nenhum hardcode de infraestrutura no texto.
- O preview do admin é **byte-idêntico** ao request real (mesmo hash) no modo correspondente.
- O Test Bench reproduz o resultado a partir da versão do perfil e do `ModelBinding` registrados.
- O linter bloqueia modelo, porta, contexto, temperatura, caminho e limites numéricos no prompt.
- `--harness 0` desliga as caudas e `--max-tokens` vale no repasse; ambos cobertos por teste de regressão.
- A bridge continua protocol-only e os cenários existentes permanecem verdes.
- Todo run tem evidência persistida com hash, modelo, perfil e resultado.

### 31.9. Riscos e mitigação

- **Depender de `tool_choice=required`:** não depender. Preferir harness nativo do cliente; no modo bridge, usar tool policy + nudge determinístico + critério de aceite real.
- **Divergência nativo × custom:** captura e diff automáticos no admin.
- **Proliferação de perfis:** versionamento imutável, aprovação e catálogo único.
- **Admin virar segundo cérebro:** composição na Application, bridge intacta, sem regra de negócio no protocolo.
- **Custo de geração longa:** `max_tokens` explícito por binding e detecção de stall, com evidência de tempo/tokens por run.

### 31.10. Diretriz final da seção

O harness deve ser tratado como **produto da plataforma**, não como texto de emergência: versionado, testável, observável e customizável no admin, com a bridge permanecendo pequena e protocol-only. A experiência desta sessão mostra que o melhor caminho é **começar pelo harness nativo do cliente** e só sintetizar um harness próprio quando o cliente não trouxer um — sempre sem hardcodes e sempre com evidência reproduzível.

---

## 32. Estado de implementação — as-built (revisão 2026-10-20)

> Seção incremental de rastreabilidade. Registra **o que existe hoje no código** de `OpenCodeBridge/AI.Manager`, com evidência de arquivo. Não altera os requisitos das seções 1–31: marca cada um como Concluído, Parcial, Adaptado ou Não iniciado e aponta o desvio quando houver.

### 32.1 Escopo e método

- Fonte: inspeção direta de `*.cs`, `*.cshtml`, `*.csproj`, do schema em `Rochas.AI.Manager.Infrastructure/Data/AppDb.cs` e das rotas em `Program.cs`.
- A suíte xUnit **não foi executada** nesta revisão. Os números de teste são de inventário estático (`[Fact]`/`[Theory]`); o resultado deve ser registrado pelo CI.
- Regra mantida (seção 28.1): nenhuma capacidade é considerada concluída por README, seeder ou código não exercitado.

### 32.2 Mapa real da solução

| Projeto | Papel | TFM | Dependências principais |
|---|---|---|---|
| `Rochas.AI.Manager.Domain` | Entidades, base classes, contratos | net10.0 | `Rochas.Data.Specification` 1.6.3 |
| `Rochas.AI.Manager.Application` | Serviços de negócio, tools handlers, helpers | net10.0 | `Rochas.DapperRepository` 2.0.5, `Microsoft.Data.Sqlite` 10.0.12 |
| `Rochas.AI.Manager.Infrastructure` | `AppDb`, `ReportService`, `PasswordHasherService` | net10.0 | `Rochas.DapperRepository`, `Microsoft.Data.Sqlite`, `Rochas.PDFGenerator` 1.3.0, `Rochas.ExcelToJsonParser` 1.6.4 |
| `Rochas.AI.Manager.Api` | Web API, Swagger, proxy OpenAI-compatible | net10.0 | `JwtBearer` 10.0.13, `Swashbuckle` 10.3.1, `Serilog` 4.4.0, `Rochas.Telemetry` 1.1.1 |
| `Rochas.AI.Manager.Web` | MVC + Razor + AJAX | net10.0 | idem + `ExcelToJsonParser` |
| `Rochas.AI.Manager.TrainingEngine` | Backend numérico, runner de processo | net10.0 | `TorchSharp` 0.107.0 (referenciado, **não usado** no backend atual) |
| `tests/Rochas.AI.Manager.Tests` | xUnit | net10.0 | xunit 2.9.3, Test.Sdk 17.14.1, coverlet 6.0.4 |

Desvio estrutural: **não existe o projeto `TrainingWorker`** previsto na seção 4; não existem as pastas `InferenceServers`, `Models/{Llama,Qwen,DeepSeek}`, `Adapters/LoRA`, `Checkpoints` nem `Tokenization`.

### 32.3 Matriz de rastreabilidade v1 → código

| Seção v1 | Estado | Evidência | Desvio |
|---|---|---|---|
| §4 Estrutura-alvo | Adaptado | 7 projetos em `AI.Manager/src` e `tests` | Sem `TrainingWorker`; estrutura por agregado, não por área |
| §5 Camadas | Concluído | `Domain` não referencia MVC/Dapper; `Web`/`Api` delegam a `Application` | `Application` referencia `Microsoft.Data.Sqlite` (vazamento de infra) |
| §6 DDD/Generic Repo/UoW | Parcial | `Domain/Common/IRepository.cs`, `IUnitOfWork.cs` | Contratos próprios **não são usados**; serviços usam `IGenericRepository`/`IPersistenceRepository` do pacote; UoW inexistente na prática; repositórios especializados não existem |
| §7 MVC e menus | Parcial | 18 controllers, 22 views, `_Layout.cshtml` | Sem Dashboard/Modelos/Checkpoints/Avaliações/Estatísticas/Configurações; `/Reports` e `/Approvals` existem **fora do menu**; rota default é `/Harness` |
| §8 AJAX puro | Concluído | `fetch` nas views; `afHeaders()` e antiforgery nos POSTs Web | Sem SignalR/polling estruturado |
| §9 Controllers previstos | Adaptado | Controllers por agregado (`HarnessController`, `TrainingWorkflowController`, etc.) | `DashboardController`, `StatisticsController`, `EvaluationController`, `SettingsController` ausentes |
| §10 API e operações | Parcial | `/api/{agents,datasets,experiments,harness*,model*,toolpolicies,trainingjobs,trainingpatterns,users,reports,tools}` + `/v1/chat/completions` | Sem `/api/profiles/{id}/assemble`, `/api/training/estimate`, `/api/statistics`, `/api/chat/test` |
| §11 Execução em segundo plano | Não iniciado | `TrainingEngine.RunAsync` chamado por `POST /api/trainingjobs/{id}/run` | Execução **dentro da requisição**; sem fila persistente, outbox, worker, exclusividade de GPU ou cancelamento cooperativo |
| §12 TorchSharp multi-modelo | Não iniciado | `TorchSharpTrainingBackend` usa `double[]` (MLP 1 camada oculta, ReLU, MSE, Adam) | Sem Safetensors, tokenizer ou arquiteturas; `TorchSharp` referenciado e não utilizado |
| §13 Fine-tuning/LoRA | Não iniciado | `TrainingEngine` modo avançado executa `python3` (`QLORA_SCRIPT`, default `sft_train.py`) | Contradiz "sem Python no produto final"; nenhum adaptador LoRA em C# |
| §14 Hardware | Parcial | `HardwareService.DetectAsync` retorna máquina/OS/CPUs/working set | Sem GPU, VRAM, driver NVIDIA, CUDA ou preflight |
| §15 / §28.6 Harness supervisor | Parcial | `HarnessProfile*`, `ToolPolicy`, `ModelBinding`, `HarnessRun`, `BridgeCapture`, `HarnessLinter` | Harness **de desenvolvimento** modelado; supervisor de experimentos não existe |
| §16 / §28.4 Bridge OpenCode | Concluído (adaptado) | `Api/Controllers/ProxyController.cs` (`/v1/chat/completions`), `Helpers/ReasoningSanitizer.cs`, `Helpers/ToolCallCoercer.cs`, `Tools/*` (9 handlers + `BridgeToolExecutor`) | Sem `/v1/responses`; sem `--harness`, `--diagnostic` ou `--max-tokens`; upstream fixo por provider |
| §17 Testes | Parcial | 61 casos em 12 arquivos; `SequentialCollection` para integração | Sem testes de Web/E2E, sem contrato da bridge, sem teste de carga |
| §21 Múltiplos modelos/servidores | Parcial | `ModelBinding` (provider/modelo/contexto/thinking/temp), roteamento `llama` → `:4125`, demais → `:4124` | Sem Ollama; sem seleção por request; sem validação de disponibilidade |
| §22 Perfis de instrução | Concluído | `InstructionProfile` + serviço + UI + `TrainingPattern` | Sem "assemble" de perfil |
| §23 Básico vs avançado | Parcial | `TrainingJob.AdvancedMode`, `TrainingMethod`, `ConfigurationJson` | Modo avançado delega a script externo; básico não deriva hiperparâmetros do perfil |
| §24 Fluxo de montagem | Não iniciado | — | `TrainingPattern` existe, mas não há fluxo de montagem implícita |
| §25 Armazenamento/reprodutibilidade | Parcial | Metadados de jobs, hashes, `BridgeCapture`, `HarnessRun` | Sem catálogo de evidências por experimento; captura não é vinculada ao run |
| §26 Estatísticas admin | Parcial | `/Reports` (preview + export CSV/XLSX/PDF/HTML) | Sem painel, sem agregações, sem séries temporais; filtros de data ignorados |
| §27 Testes no chat web UI | Não iniciado | `Views/Chat/Index.cshtml` (1.498 linhas) | A view chama `/Chat/{Stream,Sessions,Config,Approve,Calibrate,Stop,ClientLog}`, **rotas que não existem** no AI.Manager; `ChatController` só tem `Index` → chat não funcional hoje |
| §28.9 Catálogo de modelos | Parcial | `ModelArtifact`, `ModelBinding`, `Agent`, seeders | Sem configuração de runtime/quantização/driver por execução |
| §28.10 Dashboard operacional | Não iniciado | `/health` com `DIAGNOSTICS_ENABLED` | Sem estado de servidores, GPU, worker ou alertas |
| §29 Componentes Rochas.* | Parcial | `DapperRepository`, `PDFGenerator`, `ExcelToJsonParser`, `Data.Specification`, `Telemetry` | `Rochas.ImageGenerator` e `Rochas.CacheIndexer` ausentes |
| §30 Multimídia/áudio | Não iniciado | — | — |
| §31 Harness como produto | Concluído (modelo) / Parcial (UI) | `Harness/HarnessProfile*.cs`, `ToolPolicy`, `ModelBinding`, `HarnessRun`, `BridgeCapture`, `HarnessLinter` (6 regras), UI com lista/versão/aprovação/lint/preview | Sem Test Bench, sem matriz de capacidade por modelo, sem geração de `agents/*.md`/`opencode.json` |

### 32.4 Domínio implementado (19 tabelas)

Base classes: `BaseEntity` (id, parent_id, code, created_at, name, is_active), `AuditableEntity` (owner_login, updated_at, updater_id, last_update), `MultiEntityReferrer` (entity_hash_code), `WorkFlowBasedEntity` (status_id, next_status_id, requester_id, approver_id, request_note, approval_note, approved).

| Grupo | Entidade / tabela | Campos próprios relevantes |
|---|---|---|
| Cadastros | `User` / `users` | email, password_hash, is_admin |
| | `Agent` / `agents` | model, bridge_url, temperature, thinking, system_prompt, mode, tools |
| Catálogo | `ModelArtifact` / `model_artifacts` | family, variant, format, file_path, file_hash, is_trainable, status |
| | `Dataset` / `datasets` | format, version, file_path, file_hash, is_validated |
| Perfis | `InstructionProfile` / `instruction_profiles` | description, instructions_json, version, status |
| | `TrainingPattern` / `training_patterns` | profile_id → instruction_profiles, model_artifact_id → model_artifacts, pattern_json |
| Experimento | `Experiment` / `experiments` | hypothesis, base_artifact_id, tuned_artifact_id, metrics_json, status, rejection_reason |
| Treino | `TrainingJob` / `training_jobs` | status, model_artifact_id, dataset_id, experiment_id, profile_name, advanced_mode, training_method, configuration_json, started_at, completed_at, error_message, last_loss, vram_used_mb, epochs_completed, best_accuracy, throughput_tokens_per_sec |
| Harness | `HarnessProfile` / `harness_profiles` | description, status |
| | `HarnessProfileVersion` / `harness_profile_versions` | profile_id, version, mode, checksum, approved_by, approved_at |
| | `HarnessAttachment` / `harness_attachments` | version_id, kind, role, content, char_count |
| | `ToolPolicy` / `tool_policies` | role, allow, deny, require_call, max_turns, stall_limit |
| | `ModelBinding` / `model_bindings` | provider, model_id, max_tokens, context_window, thinking, temperature |
| | `HarnessRun` / `harness_runs` | evidence_id, profile_version_id, model_binding_id, request_hash, tool_calls, files_created, result_code, duration_ms, tokens_per_second |
| | `BridgeCapture` / `bridge_captures` | mode, request_json, response_json, request_hash |
| Workflow | `TrainingWorkflowCard` / `training_workflow_cards` | title, description, experiment_id, accuracy, target_accuracy, status_id, next_status_id, requester_id, approver_id, approved, rejection_reason, owner_login |
| | `EntityStatus` / `entity_statuses` | code, description |
| | `WorkflowStep` / `workflow_steps` | entity_hash_code, sort_order, status_id, next_status_id, custom_rule |
| | `WorkFlowTransition` / `workflow_transitions` | entity_hash_code, entity_id, entity_status_from/to, action, approver_id, note |
| Relatórios | `ReportQuery`, `ReportResult`, `IReportService` | contrato em `Domain/Interfaces/IReportService.cs` (não é tabela) |

FKs são **lógicas**: `AppDb.Init` cria as tabelas com `CREATE TABLE IF NOT EXISTS` e não declara `FOREIGN KEY`. `MultiEntityReferrer.entity_hash_code` é calculado por `Domain/Helpers/EntityHashCode.cs` (djb2 sobre o nome completo do tipo).

### 32.5 Persistência real

- Banco: SQLite, `Data Source={AppContext.BaseDirectory}/ai.manager.db;Cache=Shared`, `PRAGMA journal_mode=WAL; synchronous=NORMAL; busy_timeout=10000`.
- Schema criado em runtime por `AppDb.Init()` com `CREATE TABLE IF NOT EXISTS`; migração apenas ad-hoc (`ALTER TABLE training_jobs ADD COLUMN training_method` em `try/catch`).
- Repositórios: `GenericRepository<T>` do pacote `Rochas.DapperRepository`, registrados manualmente no DI de `Web` e `Api` (cerca de 20 registros por app).
- `IUnitOfWork`/`IRepository<T,TId>` do domínio não são resolvidos no DI; não há transação explícita entre agregados.
- `Domain` não conhece persistência; porém `Application` referencia `Microsoft.Data.Sqlite` e `Rochas.DapperRepository` (limite de camada violado).

### 32.6 Serviços implementados

`Application/Services` (19): `AccuracyEvaluator`, `AgentService`, `BridgeCaptureService`, `DatasetService`, `ExperimentService`, `HardwareService`, `HarnessAttachmentService`, `HarnessLinter`, `HarnessProfileService`, `HarnessRunService`, `InstructionProfileService`, `ModelArtifactService`, `ModelBindingService`, `ToolPolicyService`, `TrainingJobService`, `TrainingPatternService`, `TrainingWorkflowService`, `UserService`, `WorkFlowBasedService<T>`.
`Infrastructure/Services` (2): `PasswordHasherService`, `ReportService`.
`TrainingEngine` (3): `TrainingEngine`, `TrainingRunner`, `TorchSharpTrainingBackend`.
`Application/Tools` (16 arquivos): `BridgeToolExecutor`, `IToolHandler`, `ToolContext`, `ToolResult`, `ProcessRunner`, `ShellInspector`, `WorkspaceGuard` e handlers `Shell/Read/Write/Edit/Grep/Glob/Fetch/Git/Subagent`.

Cobertura de casos de uso: CRUD + transições de estado (Draft→Approved→Deprecated; Planned→Running→Completed→Approved/Rejected; Queued→Running→Completed/Failed/Cancelled) + avaliação de acurácia (F1 por tokens) + exportação de relatório.

### 32.7 Superfície Web e API (as-built)

- **Api (21 controllers)**: agents, bridgecaptures, datasets (+`validate`, `validate-format`), experiments (+start/complete/approve/reject), hardware (`detect`), harness (`{id}/content`), harnessattachments, harnesslinter, harnessprofiles (+versions/approve), harnessruns (+`version/{id}`), instructionprofiles (+approve/deprecate), modelartifacts (+approve/deprecate), modelbindings, reports (`preview`, `export`), toolpolicies, tools (`execute`), trainingjobs (+cancel/start/progress/complete/fail/run), trainingpatterns, trainingworkflow (move/accuracy/evaluate/reject), users, proxy `/v1/chat/completions`.
- **Web (18 controllers)**: Account, Agents, Approvals (só `Index`, `[Authorize(Roles="Admin")]`), BridgeCaptures, Chat (só `Index`), Datasets, Experiments, Harness (Index/ListPartial/Create/AddVersion/Approve/Lint/Preview), HarnessRuns, InstructionProfiles, ModelArtifacts, ModelBindings, Reports (Index/Preview/Export), ToolPolicies, TrainingJobs, TrainingPatterns, TrainingWorkflow (Index/Create/Move/UpdateAccuracy/Reject/Evaluate), Users.
- **Views (22)**: uma `Index` por módulo, `Harness/_ProfileList`, `Harness/_ProfilePreview`, `Reports/_ReportPreview`, `Shared/_Layout`, `Account/Login`.
- **Autenticação**: Web com cookie (`/Account/Login`, cookie `ai.manager`) + JWT opcional; Api com JWT Bearer. Chave simétrica **hardcoded** nos dois `Program.cs`.

### 32.8 Harness (seção 31) — o que já é código

- Modelo: perfil → versão (mode, checksum, approved_by/at) → anexos (kind, role, char_count) + tool policies por role + model bindings + runs + capturas.
- Linter de hardcodes com 6 regras (porta, contexto `NNk`, temperatura, caminho local, nome de modelo/provider, número grande) — `HarnessLinter.FindHardcodes`, usado também no seeder para abortar seed com hardcode.
- UI administrativa: lista de perfis, criação, nova versão, aprovação, lint e preview do conteúdo (`api/harness/{id}/content`).
- Seeders: `HarnessSeeder` cria "OpenCode Summarized" em modo `BridgeBypass`; `BridgeFeatureSeeder` cria "OpenCodeBridge Agents" em modo `ClientNative` com anexos Core/Tools/Nucleo e 6 tool policies (build, plan, orch, moe, scripter, exec); `HarnessScenarioSeeder` cria cenários.
- Não implementado: Test Bench, execução canônica por faixa, diff entre versões no admin, matriz de capacidade por modelo, geração de `agents/*.md`/`opencode.json`, `X-Harness-Profile`.

### 32.9 Workflow e auditoria

- `TrainingWorkflowCard` é a única entidade que herda `WorkFlowBasedEntity`; fluxo: `Create` (status 1) → `Move` (grava `WorkFlowTransition` com `approver_id = 0`) → `UpdateAccuracy` → `Evaluate`/`Reject`.
- `entity_statuses` semeado com 13 códigos: DRAFT, APPROVED, DEPRECATED, PLANNED, RUNNING, COMPLETED, REJECTED, QUEUED, CANCELLED, FAILED, NOVO, ACEITE, REPROVADO.
- `workflow_steps` semeado por entidade (ModelArtifact, InstructionProfile, HarnessProfile, Experiment, TrainingWorkflowCard) com transições permitidas, mas `WorkFlowBasedService` não é registrado no DI nem chamado pelos controllers — a validação de passo é feita por `if` nos serviços.
- `ApprovalsController` consulta diretamente repositórios (não usa serviços) e filtra `StatusId == 11` para cards.

### 32.10 Relatórios (seções 26 e 28.10)

- Contrato: `ReportQuery` (Entity, Status, DateFrom, DateTo, AccuracyBand/Min/Max, ModelFamily, Mode, UserId, AgentId, Page, PageSize) e `ReportResult` (Items, Total, Page, PageSize).
- Implementação: `ReportService` mapeia 13 entidades para tabela e tipo, consulta via repositório genérico, monta dicionários por coluna e pagina **em memória**.
- Exportação: CSV (`;`), XLSX e PDF via `Rochas.ExcelToJsonParser`/`Rochas.PDFGenerator`; HTML como fallback. O controller rotula PDF como `text/html`.
- **Gap crítico:** apenas `Status` é aplicado. `DateFrom`, `DateTo`, `AccuracyBand`, `AccuracyMin/Max`, `ModelFamily`, `Mode`, `UserId` e `AgentId` são **ignorados**; não há join nem agregação; `HasColumn` sempre retorna `false`.
- UI: página `/Reports` com iframe de preview e botões de exportação; **não está no menu lateral**.

### 32.11 Motor de treinamento (seções 11–13)

- `POST /api/trainingjobs/{id}/run` chama `ITrainingEngine.RunAsync` na própria requisição HTTP.
- Modo básico: MLP em `double[]` (hidden 16, 10 épocas, Adam, MSE), checkpoint serializado em memória e **não persistido**; sem dataset, gera 256 amostras sintéticas; `vram_used_mb` é valor sintético (`4096 + 256*época`).
- Modo avançado: `python3 <QLORA_SCRIPT|sft_train.py> --dataset ... --method ...` via `TrainingRunner`.
- Não há fila, lock de GPU, retomada, verificação de checkpoint ou avaliação contra referência externa.

### 32.12 Testes (seção 17)

- 61 casos (`[Fact]`/`[Theory]`) em 12 arquivos: AccuracyEvaluator (9), EntityHashCode (3), ExtraService (15), HarnessProfileService (3), Helpers (7), ReportServiceIntegration (4), ServiceIntegration (13), TrainingEngine (1), TrainingWorkflowService (5), UnitTest1 (1).
- `SequentialCollection` serializa os testes de integração que compartilham SQLite.
- Ausentes: testes de controllers/rotas, testes de contrato do proxy `/v1/chat/completions`, testes de exportação binária, testes de concorrência e de recuperação de estado.

### 32.13 Desvios e riscos abertos

| # | Risco | Evidência | Severidade | Mitigação |
|---|---|---|---|---|
| D-01 | Execução de treino dentro da requisição HTTP | `TrainingJobsController.Run` → `TrainingEngine.RunAsync` | Alta | Fila + worker (seção 11) |
| D-02 | "Sem Python" violado no modo avançado | `TrainingEngine.cs` (`QLORA_SCRIPT`) | Alta | Remover caminho Python ou reclassificar como experimento |
| D-03 | `TorchSharp` referenciado e não usado | `TrainingEngine.csproj`; backend em `double[]` | Alta | Prova sintética TorchSharp antes de prometer treino |
| D-04 | Filtros de relatório silenciosamente ignorados | `ReportService.QueryAsync` | Alta | Implementar De/Até + facetas (REPORT-PENDINGS.md) |
| D-05 | JWT com chave fixa no código | `Api/Program.cs`, `Web/Program.cs` | Alta | Configuração/secret store |
| D-06 | Credenciais seed `admin/admin` e `teste@e2e.local/Teste@123` | `Web/Program.cs` | Média | Exigir troca no primeiro acesso; remover em produção |
| D-07 | Proxy aceita qualquer Bearer e injeta `guest@123` no upstream | `ProxyController.cs` | Média | Validar token; configurar credencial do upstream |
| D-08 | Binding escolhido é sempre o primeiro, ignorando o `model` do request | `ProxyController.cs` (`bindings.FirstOrDefault()`) | Média | Resolver binding por modelo/role |
| D-09 | `HarnessRun` gravado com `ProfileVersionId=0` e `EvidenceId=0` | mesmo arquivo | Média | Vincular perfil e captura reais |
| D-10 | Chat UI órfã (7 rotas inexistentes) | `Views/Chat/Index.cshtml` vs `ChatController` | Média | Portar endpoints ou remover a view |
| D-11 | Contratos `IRepository`/`IUnitOfWork` mortos | `Domain/Common` não resolvido no DI | Baixa | Remover ou implementar |
| D-12 | `Application` referencia SQLite/Dapper | `Application.csproj` | Baixa | Mover para Infrastructure |
| D-13 | Relatórios carregam a tabela inteira em memória | `ReportService` | Média | Paginação/agregação em SQL |
| D-14 | Sem FKs físicas nem testes de integridade referencial | `AppDb.Init` | Média | Relatório de órfãos + validação de escrita |

### 32.14 Checklist para fechar o v1

1. Worker/fila persistente e cancelamento cooperativo (D-01).
2. Decisão formal sobre o caminho Python do modo avançado (D-02).
3. Prova TorchSharp (forward/backward/Adam) substituindo o MLP (D-03).
4. Filtros De/Até e facetas de relatório implementados (D-04).
5. Segurança: chave JWT, credenciais seed, autorização do proxy (D-05 a D-07).
6. Vínculo perfil/versão/captura em `HarnessRun` + Test Bench (D-09, §31.4).
7. Chat: portar `/Chat/{Stream,Sessions,Config,Approve,Calibrate,Stop,ClientLog}` ou retirar a view (D-10).
8. Dashboard operacional e estatísticas agregadas (§26/§28.10).
9. Testes de contrato da bridge e de rotas Web/API.
10. `Rochas.ImageGenerator`/`Rochas.CacheIndexer` e multimídia (§29/§30), quando priorizados.

### 32.15 Relação com o v2

O v2 (`Rochas.AI.Manager-Arquitetura v2.md`) reorganiza a arquitetura-alvo já incorporando este as-built: mantém as seções 1–31 como histórico de intenção, promove o que foi construído a "baseline", fixa decisões (ADRs), define o subsistema de relatórios e propõe o roadmap de fechamento. As pendências de relatórios, com catálogo de facetas e recortes temporais, estão em `REPORT-PENDINGS.md`.
