# JobAgent Backend

API REST e agente em segundo plano do JobAgent, em .NET 10. Este repositório não contém nem publica a aplicação Angular.

## Executar localmente

1. Instale o .NET 10 SDK.
2. Crie os arquivos locais de configuração a partir dos exemplos:

   ```powershell
   Copy-Item appsettings.example.json appsettings.json
   Copy-Item src/JobAgent.WebApi/appsettings.example.json src/JobAgent.WebApi/appsettings.json
   ```

3. Configure login e provedores no ambiente local. Não coloque credenciais no frontend.

   No PowerShell, configure o cliente OAuth do Google, o e-mail que será o administrador inicial e
   uma chave JWT aleatória com pelo menos 32 bytes:

   ```powershell
   $env:Auth__GoogleClientId = "<Google OAuth Client ID>"
   $env:Auth__AdminEmail = "seu-email@gmail.com"
   $env:Auth__JwtSigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
   ```

   No Google Cloud Console, autorize a origem JavaScript `http://localhost:4200` para o Client ID.
   Para produção, use as origens reais do frontend e mantenha esses valores em variáveis seguras do
   provedor de hospedagem.
4. Inicie a API:

   ```powershell
   dotnet run --project src/JobAgent.WebApi
   ```

   A API e o Scalar/OpenAPI ficam disponíveis em `http://localhost:5065` e `http://localhost:5065/scalar/v1`.

## Frontend separado

Em desenvolvimento, o Angular em `http://localhost:4200` usa o proxy configurado em `proxy.conf.json`, que encaminha `/api` para `http://localhost:5065`.

Para hospedar a interface e a API em origens diferentes:

- Configure `apiBaseUrl` em `public/config.js` com a URL completa da API, incluindo `/api`.
- Configure `Cors:AllowedOrigins` no backend com a origem exata do frontend. Em produção, use a variável de ambiente `Cors__AllowedOrigins__0`.
- Não coloque chaves de Gemini, Anthropic, Telegram ou e-mail no frontend.
- Configure credenciais de provedores de IA no servidor, por exemplo `Gemini__ApiKey`; elas não são
  devolvidas pela API nem podem ser alteradas pelo navegador.

## Beta por convite

- O primeiro acesso exige login Google. O e-mail definido em `Auth__AdminEmail` cria a conta
  administradora; os demais usuários precisam de um convite emitido pelo administrador.
- Cada conta começa com uma busca. Uma tentativa aceita consome um crédito; somente o administrador
  pode liberar créditos adicionais.
- Cada conta pode salvar no máximo 10 vagas, somando todos os perfis. Os limites são verificados no
  backend, não apenas na interface.
- Perfis e vagas são isolados por usuário. A busca automática e a leitura de alertas de e-mail estão
  desativadas; cada busca é iniciada manualmente por uma conta autenticada.
- O perfil profissional é enviado ao provedor de IA configurado no servidor para avaliar vagas.
  Informe os usuários sobre esse processamento antes de convidá-los.

## Banco de dados

SQLite continua disponível para desenvolvimento local. Para PostgreSQL, configure
`ConnectionStrings__JobAgent` com a connection string fornecida pelo host; a presença dessa variável
seleciona PostgreSQL automaticamente. Proteja-a como segredo e não a versione.

O banco PostgreSQL começa com um esquema vazio. No SQLite existente, o primeiro login do
administrador importa os perfis e, no máximo, as 10 vagas antigas de maior nota; não apaga os demais
registros. A cópia automática de um arquivo SQLite local para PostgreSQL ainda não está disponível:
preserve o backup e faça essa migração antes de apontar produção para PostgreSQL.

## Dados locais

Os arquivos `appsettings.json`, `jobs.db` e `profile/candidate.md` são locais e ignorados pelo Git. Use os arquivos `appsettings.example.json` como base; não versione credenciais nem dados pessoais.

## Endpoints

A API fornece login em `/api/auth`, conta em `/api/account`, administração em `/api/admin` e
endpoints autenticados em `/api/jobs`, `/api/profile` e `/api/orchestrator`. A configuração de
provedores é restrita ao administrador. O contrato completo está no Scalar em ambiente de desenvolvimento.

## Perfis e fontes

Os perfis salvos incluem nome, Markdown e palavras-chave de inclusão/exclusão. Ao trocar de perfil, listagens, detalhes, estatísticas e alterações de status ficam limitados ao perfil ativo. A inicialização migra vagas existentes para `Perfil padrão`; os registros antigos não são apagados.

O coletor automático consulta a busca pública da Gupy. InfoJobs, Catho e Indeed aparecem como buscas externas no frontend, mas não são coletados automaticamente nesta versão: as rotas públicas verificadas apresentaram restrições. Não há tentativa de contornar bloqueios; feeds RSS personalizados continuam configuráveis.
