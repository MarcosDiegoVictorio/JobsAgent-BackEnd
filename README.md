# JobAgent Backend

API REST e agente em segundo plano do JobAgent, em .NET 10. Este repositório não contém nem publica a aplicação Angular.

## Executar localmente

1. Instale o .NET 10 SDK.
2. Crie os arquivos locais de configuração a partir dos exemplos:

   ```powershell
   Copy-Item appsettings.example.json appsettings.json
   Copy-Item src/JobAgent.WebApi/appsettings.example.json src/JobAgent.WebApi/appsettings.json
   ```

3. Configure suas chaves e integrações pelo painel do frontend ou nos arquivos locais ignorados pelo Git.
4. Inicie a API:

   ```powershell
   dotnet run --project src/JobAgent.WebApi
   ```

   A API e o Scalar/OpenAPI ficam disponíveis em `http://localhost:5065` e `http://localhost:5065/scalar/v1`.

Para executar o worker sem a API, prepare a configuração local do worker e rode:

```powershell
Copy-Item src/JobAgent.Worker/appsettings.example.json src/JobAgent.Worker/appsettings.json
dotnet run --project src/JobAgent.Worker
```

## Frontend separado

Em desenvolvimento, o Angular em `http://localhost:4200` usa o proxy configurado em `proxy.conf.json`, que encaminha `/api` para `http://localhost:5065`.

Para hospedar a interface e a API em origens diferentes:

- Configure `apiBaseUrl` em `public/config.js` com a URL completa da API, incluindo `/api`.
- Configure `Cors:AllowedOrigins` no backend com a origem exata do frontend. Em produção, use a variável de ambiente `Cors__AllowedOrigins__0`.
- Não coloque chaves de Gemini, Anthropic, Telegram ou e-mail no frontend.

## Dados locais

Os arquivos `appsettings.json`, `jobs.db` e `profile/candidate.md` são locais e ignorados pelo Git. Use os arquivos `appsettings.example.json` como base; não versione credenciais nem dados pessoais.

## Endpoints

A API fornece endpoints em `/api/jobs`, `/api/config`, `/api/profile` e `/api/orchestrator`. O contrato completo está no Scalar em ambiente de desenvolvimento.

## Perfis e fontes

Os perfis salvos incluem nome, Markdown e palavras-chave de inclusão/exclusão. Ao trocar de perfil, listagens, detalhes, estatísticas e alterações de status ficam limitados ao perfil ativo. A inicialização migra vagas existentes para `Perfil padrão`; os registros antigos não são apagados.

O coletor automático consulta a busca pública da Gupy. InfoJobs, Catho e Indeed aparecem como buscas externas no frontend, mas não são coletados automaticamente nesta versão: as rotas públicas verificadas apresentaram restrições. Não há tentativa de contornar bloqueios; feeds RSS personalizados continuam configuráveis.
