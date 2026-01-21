using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Data.SqlClient;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SWWhatsAppLambdaSQSEnvio
{
    public class OutboundCollector
    {
        private static readonly IAmazonSQS _sqsClient = new AmazonSQSClient();
        private const string SQS_URL = "<SQS_URL>"; // URL da fila SQS FIFO de saída
        private const string CONN_STRING = "<CONN_STRING>";

        /// <summary>
        /// Handler que executa um loop de polling para processar mensagens do banco
        /// </summary>
        public async Task FunctionHandler(ILambdaContext context)
        {
            // O loop permite que a Lambda processe várias vezes na mesma execução (custo-benefício)
            // 20 ciclos de 3 segundos = Aproximadamente 60 segundos de atividade
            for (int i = 0; i < 20; i++)
            {
                // Segurança: Verifica se a Lambda ainda tem tempo de execução sobrando
                if (context.RemainingTime.TotalSeconds < 5) break;

                await ProcessarCiclo(context);

                if (i < 19)
                {
                    await Task.Delay(3000); // Espera 3 segundos entre as consultas ao banco
                }
            }
        }

        /// <summary>
        /// Busca mensagens pendentes no SQL e as despacha para o SQS
        /// </summary>
        private async Task ProcessarCiclo(ILambdaContext context)
        {
            try
            {
                using var connection = new SqlConnection(CONN_STRING);
                await connection.OpenAsync();

                var mensagensParaEnviar = new List<OutboundData>();

                // 1. Coleta e reserva registros. 
                // A query deve usar UPDATE com a cláusula OUTPUT para garantir atomicidade (evitar processar o mesmo registro em duas Lambdas)
                var query = @"
                    DECLARE @Said ...;";

                using (var command = new SqlCommand(query, connection))
                {
                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        mensagensParaEnviar.Add(new OutboundData
                        {
                            CodSysFilaEnvioMensagens = reader.GetInt64(0),
                            Url = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Header = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            Body = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            Instancia = reader.IsDBNull(4) ? "default" : reader.GetString(4),
                            DataCriacao = reader.GetDateTime(5)
                        });
                    }
                }

                if (mensagensParaEnviar.Count == 0) return;

                context.Logger.LogLine($"Ciclo: Coletadas {mensagensParaEnviar.Count} mensagens.");

                foreach (var msg in mensagensParaEnviar)
                {
                    try
                    {
                        string jsonBody = JsonSerializer.Serialize(msg);

                        var sqsRequest = new SendMessageRequest
                        {
                            QueueUrl = SQS_URL,
                            MessageBody = jsonBody,
                            // MessageGroupId garante a ordem de entrega para a mesma 'instância' (número de WhatsApp)
                            MessageGroupId = msg.Instancia,
                            // DeduplicationId evita duplicidade no SQS FIFO (ID do registro + carimbo de tempo)
                            MessageDeduplicationId = $"{msg.CodSysFilaEnvioMensagens}_{msg.DataCriacao.Ticks}"
                        };

                        await _sqsClient.SendMessageAsync(sqsRequest);

                        // Status 2: Mensagem entregue com sucesso à fila de processamento
                        await ExecuteUpdate(msg.CodSysFilaEnvioMensagens, 2, null, connection);
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogLine($"Erro SQS no ID {msg.CodSysFilaEnvioMensagens}: {ex.Message}");
                        // Status -99: Falha ao tentar mover do Banco para o SQS
                        await ExecuteUpdate(msg.CodSysFilaEnvioMensagens, -99, "Erro SQS: " + ex.Message, connection);
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"ERRO CRÍTICO NO CICLO: {ex.Message}");
            }
        }

        private async Task ExecuteUpdate(long cod, int status, string erro, SqlConnection conn)
        {
            var sql = "UPDATE ...";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@cod", cod);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@erro", (object)erro ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public class OutboundData
    {
        public long CodSysFilaEnvioMensagens { get; set; }
        public string Url { get; set; }
        public string Header { get; set; }
        public string Body { get; set; }
        public string Instancia { get; set; }
        public DateTime DataCriacao { get; set; }
    }
}