﻿using BotSharp.Core.Engines;
using BotSharp.Models.NLP;
using BotSharp.Platform.Abstraction;
using BotSharp.Platform.Models;
using BotSharp.Platform.Models.AiRequest;
using BotSharp.Platform.Models.AiResponse;
using BotSharp.Platform.Models.MachineLearning;
using DotNetToolkit;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotSharp.Core
{
    public abstract class PlatformBuilderBase<TAgent> where TAgent : AgentBase
    {
        public TAgent Agent { get; set; }

        public IAgentStorage<TAgent> Storage { get; set; }

        private readonly IAgentStorageFactory<TAgent> agentStorageFactory;
        private readonly IPlatformSettings settings;

        public PlatformBuilderBase(IAgentStorageFactory<TAgent> agentStorageFactory, IPlatformSettings settings)
        {
            this.agentStorageFactory = agentStorageFactory;
            this.settings = settings;
        }

        public async Task<List<TAgent>> GetAllAgents()
        {
            await GetStorage();

            return await Storage.Query();
        }

        public async Task<TAgent> LoadAgentFromFile<TImporter>(string dataDir) where TImporter : IAgentImporter<TAgent>, new()
        {
            Console.WriteLine($"Loading agent from folder {dataDir}");

            var meta = LoadMeta(dataDir);
            var importer = new TImporter
            {
                AgentDir = dataDir
            };

            // Load agent summary
            var agent = await importer.LoadAgent(meta);

            // Load user custom entities
            await importer.LoadCustomEntities(agent);

            // Load agent intents
            await importer.LoadIntents(agent);

            // Load system buildin entities
            await importer.LoadBuildinEntities(agent);

            Console.WriteLine($"Loaded agent: {agent.Name} {agent.Id}");

            Agent = agent;

            return agent;
        }

        private AgentImportHeader LoadMeta(string dataDir)
        {
            // load meta
            string metaJson = File.ReadAllText(Path.Combine(dataDir, "meta.json"));

            return JsonConvert.DeserializeObject<AgentImportHeader>(metaJson);
        }

        public async Task<TAgent> GetAgentById(string agentId)
        {
            GetStorage();

            return await Storage.FetchById(agentId);
        }

        public async Task<TAgent> GetAgentByName(string agentName)
        {
            await GetStorage();

            return await Storage.FetchByName(agentName);
        }

        public virtual async Task<ModelMetaData> Train(TAgent agent, TrainingCorpus corpus, BotTrainOptions options)
        {
            if (String.IsNullOrEmpty(options.AgentDir))
            {
                options.AgentDir = Path.Combine(AppDomain.CurrentDomain.GetData("DataPath").ToString(), "Projects", agent.Id);
            }

            if (String.IsNullOrEmpty(options.Model))
            {
                options.Model = "model_" + DateTime.UtcNow.ToString("yyyyMMdd");
            }

            ModelMetaData meta = null;

            // train by contexts
            corpus.UserSays.GroupBy(x => x.ContextHash).Select(g => new
            {
                Context = g.Key,
                Corpus = new TrainingCorpus
                {
                    Entities = corpus.Entities,
                    UserSays = corpus.UserSays.Where(x => x.ContextHash == g.Key).ToList()
                }
            })
            .ToList()
            .ForEach(async c =>
            {
                var trainer = new BotTrainer(settings);
                agent.Corpus = c.Corpus;

                meta = await trainer.Train(agent, new BotTrainOptions
                {
                    AgentDir = options.AgentDir,
                    Model = options.Model + $"{Path.DirectorySeparatorChar}{c.Context}"
                });
            });

            meta.Pipeline.Clear();
            meta.Model = options.Model;

            return meta;
        }

        public virtual async Task<TResult> TextRequest<TResult>(AiRequest request)
        {
            string contexts = String.Join("_", request.Contexts);
            string contextHash = contexts.GetMd5Hash();

            Console.WriteLine($"TextRequest: {request.Text}, {contexts}, {request.SessionId}");

            // Load agent
            var projectPath = Path.Combine(AppDomain.CurrentDomain.GetData("DataPath").ToString(), "Projects", request.AgentId);
            var model = Directory.GetDirectories(projectPath).Where(x => x.Contains("model_")).Last().Split(Path.DirectorySeparatorChar).Last();
            var modelPath = Path.Combine(projectPath, model);
            request.AgentDir = projectPath;
            request.Model = model + $"{Path.DirectorySeparatorChar}{contextHash}";

            Agent = await GetAgentById(request.AgentId);

            var preditor = new BotPredictor();
            var doc = await preditor.Predict(Agent, request);

            var parameters = new Dictionary<String, Object>();
            if (doc.Sentences[0].Entities == null)
            {
                doc.Sentences[0].Entities = new List<NlpEntity>();
            }
            doc.Sentences[0].Entities.ForEach(x => parameters[x.Entity] = x.Value);

            var predictedIntent = doc.Sentences[0].Intent;

            var aiResponse = new AiResponse
            {
                ResolvedQuery = request.Text,
                Score = predictedIntent.Confidence,
                Source = predictedIntent.Classifier,
                Intent = predictedIntent.Label
            };

            Console.WriteLine($"TextResponse: {aiResponse.Intent}, {request.SessionId}");

            return await AssembleResult<TResult>(aiResponse);
        }

        public virtual async Task<TResult> AssembleResult<TResult>(AiResponse response)
        {
            throw new NotImplementedException();
        }

        public virtual async Task<bool> SaveAgent(TAgent agent)
        {
            await GetStorage();

            // default save agent in FileStorage
            await Storage.Persist(agent);

            return true;
        }

        protected async Task<IAgentStorage<TAgent>> GetStorage()
        {
            if (Storage == null)
            {
                Storage = await agentStorageFactory.Get();
            }
            return Storage;
        }
    }
}
