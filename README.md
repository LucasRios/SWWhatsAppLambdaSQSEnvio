# SW WhatsApp Outbound Collector

Este serviço funciona como o **alimentador (feeder)** do sistema de envio de mensagens. Ele faz a ponte entre o banco de dados relacional (SQL Server) e a arquitetura distribuída na AWS.

## ⚙️ Funcionamento

O componente não espera por um gatilho externo; ele é configurado para rodar em intervalos curtos (ex: via EventBridge a cada 1 minuto). Ao ser executado:
1. Inicia um loop interno de 60 segundos.
2. Consulta o banco de dados em busca de registros com `Status = 0`.
3. Reserva os registros para evitar que outras instâncias da Lambda processem os mesmos dados.
4. Publica as mensagens em uma **Fila SQS FIFO**, preservando a ordem de envio por **Instância**.
5. Atualiza o banco de dados confirmando que a mensagem entrou na esteira de processamento.

## 🛠️ Arquitetura e Resiliência

- **SQS FIFO**: Garante que as mensagens não sejam duplicadas e sejam entregues na ordem exata de criação.
- **Message Deduplication**: Utiliza uma chave composta (ID + Ticks) para evitar reenvios acidentais.
- **Atomicidade SQL**: Utiliza `UPDATE TOP (X) WITH OUTPUT` para garantir que a reserva do registro seja segura contra concorrência (Race Conditions).

## 🚀 Como Configurar

1. **Variáveis de Ambiente**:
   - `SQS_URL`: URL da fila FIFO de destino.
   - `CONN_STRING`: String de conexão com o SQL Server.
2. **Timeout da Lambda**: Deve ser configurado para pelo menos **65 segundos** (devido ao loop interno de 60s).

## 📊 Status de Processamento

| Código | Significado |
|--------|-------------|
| 0 | Pendente no Banco |
| 1 | Reservado pela Lambda (Em processamento) |
| 2 | Postado no SQS com Sucesso |
| -99 | Falha ao postar no SQS |
