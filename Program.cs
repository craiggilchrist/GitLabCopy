using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NGitLab.Models;

namespace GitlabCopy
{
	class Program
	{
		private const string oldApiKey = "YOUR_SOURCE_SERVER_API_KEY";
		private const string newApiKey = "YOUR_DESTINATION_SERVER_API_KEY";
		private const string workingDirectory = @"c:\GitCopy-Files";
		private const string doneFilePath = @"completed.txt";
		private const string oldGitUrl = "https://oldgitlab.yourcompany.com/";
		private const string newGitUrl = "https://newgitlab.yourcompany.com/";
		private const bool isMultiThreaded = true;

		private static NGitLab.GitLabClient oldApiClient;
		private static NGitLab.GitLabClient newApiClient;
		private static List<Project> oldProjects;
		private static List<Namespace> oldGroups;
		private static List<Namespace> newGroups;
		private static List<Project> newProjects;

		static async Task Main(string[] args)
		{
			if (!Directory.Exists(workingDirectory))
			{
				Directory.CreateDirectory(workingDirectory);
			}

			oldApiClient = NGitLab.GitLabClient.Connect(oldGitUrl, oldApiKey);
			newApiClient = NGitLab.GitLabClient.Connect(newGitUrl, newApiKey);

			Console.Write("Getting old groups... ");
			oldGroups = oldApiClient.Groups.Accessible().ToList();
			Console.WriteLine("Done");

			var namespaces = oldApiClient.Groups.GetNamespaces();

			Console.Write("Getting old projects... ");
			oldProjects = oldApiClient.Projects.Accessible().ToList();
			Console.WriteLine("Done");

			Console.Write("Getting new groups... ");
			newGroups = newApiClient.Groups.Accessible().ToList();
			Console.WriteLine("Done");

			Console.Write("Getting new projects... ");
			newProjects = newApiClient.Projects.Accessible().ToList();
			Console.WriteLine("Done");

			if (isMultiThreaded)
			{
				var tasks = oldGroups.Select(g =>
				{
					Action action = async () =>
					{
						Console.WriteLine("Task={0}, Group={1}, Thread={2}", Task.CurrentId, g.Name, Thread.CurrentThread.ManagedThreadId);
						await CopyGroup(g);
					};

					var task = new Task(action);
					task.Start();

					return task;
				});

				await Task.WhenAll(tasks);

				Console.WriteLine("Complete");
			}
			else
			{
				foreach (var group in oldGroups)
				{
					await CopyGroup(group);
				}
			}

			Console.ReadLine();
		}

		private static async Task CopyGroup(Namespace group)
		{
			Console.WriteLine($"Working on group: {group.Name} (/{group.FullPath})");

			var groupFilePath = Path.Combine(workingDirectory, group.FullPath);

			if (!Directory.Exists(groupFilePath))
			{
				Directory.CreateDirectory(groupFilePath);
			}

			Console.Write("Checking if group exists... ");
			var newNamespace = newGroups.FirstOrDefault(g => g.FullPath.Equals(group.FullPath, StringComparison.InvariantCultureIgnoreCase));

			if (newNamespace == null)
			{
				Console.WriteLine("It doesn't. Creating it now.");

				newNamespace = newApiClient.Groups.Create(new NamespaceCreate { Name = group.Name, Description = group.Description, Path = group.Path });

				Console.WriteLine($"Created with ID: {newNamespace.Id}");
			}
			else
			{
				Console.WriteLine("It does :)");
			}

			foreach (var project in oldProjects.Where(p => p.Namespace.Path.Equals(group.Path, StringComparison.InvariantCultureIgnoreCase)))
			{
				try
				{
					var completedProjects = await GetCompletedProjects();

					if (completedProjects.FirstOrDefault(p => p.Equals(project.PathWithNamespace, StringComparison.InvariantCultureIgnoreCase)) != null)
					{
						Console.WriteLine("Project already moved. Skipping");
						continue;
					}

					Console.Write($"Checking if project already exists: {project.Name}... ");

					var newProject = newProjects.FirstOrDefault(g => g.PathWithNamespace.Equals(project.PathWithNamespace, StringComparison.InvariantCultureIgnoreCase));

					var projectFilePath = Path.Combine(groupFilePath, project.Path);

					if (Directory.Exists(projectFilePath))
					{
						Directory.Delete(projectFilePath, true);
					}

					// Check if the project exists.
					if (newProject == null)
					{
						Console.WriteLine("New project doesn't exist. Creating it.");

						newProject = newApiClient.Projects.Create(new ProjectCreate
						{
							Name = project.Name,
							Description = project.Description,
							NamespaceId = newNamespace.Id,
							Path = project.Path
						});

						Console.WriteLine($"Project created with ID: ${newProject.Id}");
					}
					else
					{
						Console.WriteLine("Project already exists :)");
					}

					Console.WriteLine("Performing GIT operations.");

					Console.WriteLine("Cloning old project");

					var repo = oldApiClient.GetRepository(project.Id);

					await GitCommand(groupFilePath, $"clone {project.SshUrl}");

					var branches = (await GitCommand(projectFilePath, "branch -a"))
						.ToList()
						.Where(l => !l.Contains("*"))
						.Where(l => !l.Contains("->"))
						.Select(l => l.Trim().Replace("remotes/origin/", ""))
						.Where(l => l.Length > 0);

					foreach (var branchName in branches)
					{
						Console.WriteLine("Checking out: " + branchName);
						await GitCommand(projectFilePath, $"checkout {branchName}");
					}

					// Add the new remote.
					await GitCommand(projectFilePath, "remote add new " + newProject.SshUrl);

					// Push all the branches & tags.
					await GitCommand(projectFilePath, "push -u new --all");
					await GitCommand(projectFilePath, "push -u new --tags");

					await WriteToCompletedProjects(project.PathWithNamespace);
				}
				catch (Exception ex)
				{
					Console.ForegroundColor = ConsoleColor.Red;

					Console.WriteLine(ex.Message);

					Console.ForegroundColor = ConsoleColor.White;
				}
			}
		}

		private static async Task WriteToCompletedProjects(string completedProject)
		{
			var completedProjects = (await GetCompletedProjects()).ToList();
			completedProjects.Add(completedProject);
			await File.WriteAllLinesAsync(doneFilePath, completedProjects);
		}

		private static async Task<IEnumerable<string>> GetCompletedProjects()
		{
			if (!File.Exists(doneFilePath))
			{
				return new List<string>();
			}

			var lines = await File.ReadAllLinesAsync(doneFilePath);

			return lines.Select(l => l.Trim()).Where(l => l.Length > 0);
		}

		private static Task<IEnumerable<string>> GitCommand(string workingDirectory, string command, int waitTimeInMilliseconds = 7200000)
		{
			Console.WriteLine($"git {command}");

			var gitInfo = new ProcessStartInfo();
			gitInfo.CreateNoWindow = false;
			gitInfo.RedirectStandardError = true;
			gitInfo.RedirectStandardOutput = true;
			gitInfo.FileName = @"git";

			Process gitProcess = new Process();
			gitInfo.Arguments = command;
			gitInfo.WorkingDirectory = workingDirectory;

			gitProcess.StartInfo = gitInfo;
			gitProcess.EnableRaisingEvents = true;

			var tcs = new TaskCompletionSource<IEnumerable<string>>();

			var output = new List<string>();

			gitProcess.Exited += (sender, args) =>
			{
				tcs.SetResult(output);

				gitProcess.Close();
			};

			gitProcess.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
			{
				if (!string.IsNullOrEmpty(e.Data))
				{
					Console.WriteLine(e.Data);
					output.Add(e.Data);
				}
			};

			gitProcess.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
			{
				if (!string.IsNullOrEmpty(e.Data))
				{
					Console.WriteLine(e.Data);
					output.Add(e.Data);
				}
			};

			gitProcess.Start();
			gitProcess.BeginErrorReadLine();
			gitProcess.BeginOutputReadLine();

			return tcs.Task;
		}
	}
}
