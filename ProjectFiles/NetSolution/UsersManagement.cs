#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
#endregion

/// <summary>
/// NetLogic class for managing user import from JSON files.
/// This class allows batch creation of users by reading their definitions from a JSON file,
/// typically loaded from a USB drive or external storage.
/// </summary>
/// <remarks>
/// Required LogicObject variables:
/// - JsonUri (string): The ResourceUri path to the JSON file containing user definitions
/// - UserImportFeedback (string): Output variable for displaying import results to the user
/// 
/// JSON file format:
/// [
///   { "BrowseName": "username", "Group": "GroupName", "Password": "password123", "LocaleId": "en-US" },
///   ...
/// ]
/// </remarks>
public class UsersManagement : BaseNetLogic
{
    #region Constants
    private const string LOG_CATEGORY = "UsersManagement";
    private const string USERS_PATH = "Security/Users";
    private const string GROUPS_PATH = "Security/Groups";
    #endregion

    #region Private Fields
    private IUAVariable jsonUri;
    private IUAVariable userImportFeedback;
    private List<string> usersCreated;
    private List<string> usersSkipped;
    private List<string> usersWithErrors;
    #endregion

    #region Lifecycle Methods
    /// <summary>
    /// Called when the NetLogic starts. Initializes references to LogicObject variables.
    /// </summary>
    public override void Start()
    {
        // Get references to required variables from the LogicObject
        jsonUri = LogicObject.GetVariable("JsonUri");
        userImportFeedback = LogicObject.GetVariable("UserImportFeedback");

        // Validate that required variables exist
        if (jsonUri == null)
            Log.Warning(LOG_CATEGORY, "JsonUri variable not found in LogicObject");
        
        if (userImportFeedback == null)
            Log.Warning(LOG_CATEGORY, "UserImportFeedback variable not found in LogicObject");
    }

    /// <summary>
    /// Called when the NetLogic stops. Cleanup resources if needed.
    /// </summary>
    public override void Stop()
    {
        // Clear references to allow garbage collection
        jsonUri = null;
        userImportFeedback = null;
    }
    #endregion

    #region Exported Methods
    /// <summary>
    /// Main method to generate users from a JSON file.
    /// Reads the JSON file specified in the JsonUri variable and creates users that don't already exist.
    /// </summary>
    /// <remarks>
    /// This method:
    /// 1. Reads the JSON file from the specified URI
    /// 2. Validates each user entry
    /// 3. Creates users that don't already exist
    /// 4. Assigns users to their specified groups
    /// 5. Sets passwords for new users
    /// 6. Reports results via the UserImportFeedback variable
    /// </remarks>
    [ExportMethod]
    public void GenerateUsersFromJson()
    {
        // Initialize tracking lists
        usersCreated = new List<string>();
        usersSkipped = new List<string>();
        usersWithErrors = new List<string>();

        // Validate JsonUri variable
        if (jsonUri?.Value?.Value == null)
        {
            SetFeedback("Error: JSON URI is not configured");
            Log.Error(LOG_CATEGORY, "JsonUri variable is null or empty");
            return;
        }

        // Parse the ResourceUri and get the file path
        string filePath;
        try
        {
            var resourceUri = new ResourceUri(jsonUri.Value.Value.ToString());
            filePath = resourceUri.Uri;
        }
        catch (Exception ex)
        {
            SetFeedback($"Error: Invalid JSON URI format - {ex.Message}");
            Log.Error(LOG_CATEGORY, $"Failed to parse ResourceUri: {ex.Message}");
            return;
        }

        // Read users from the JSON file
        var jsonUsers = ReadUsersFromFile(filePath);
        
        if (jsonUsers == null || jsonUsers.Count == 0)
        {
            SetFeedback("No users found in the JSON file or file could not be read");
            return;
        }

        Log.Info(LOG_CATEGORY, $"Found {jsonUsers.Count} user(s) in JSON file");

        // Process each user from the JSON file
        foreach (var user in jsonUsers)
        {
            CreateUserIfNotExists(user);
        }

        // Build and set the feedback message
        BuildFeedbackMessage();
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Reads and deserializes user definitions from a JSON file.
    /// </summary>
    /// <param name="filePath">The full path to the JSON file</param>
    /// <returns>A list of JsonUser objects, or an empty list if an error occurs</returns>
    private List<JsonUser> ReadUsersFromFile(string filePath)
    {
        var users = new List<JsonUser>();

        // Validate file path
        if (string.IsNullOrEmpty(filePath))
        {
            Log.Error(LOG_CATEGORY, "File path is null or empty");
            return users;
        }

        try
        {
            // Check if file exists before attempting to read
            if (!File.Exists(filePath))
            {
                Log.Error(LOG_CATEGORY, $"JSON file not found: {filePath}");
                return users;
            }

            // Read and deserialize the JSON content
            string json = File.ReadAllText(filePath);
            
            if (string.IsNullOrWhiteSpace(json))
            {
                Log.Error(LOG_CATEGORY, "JSON file is empty");
                return users;
            }

            users = JsonConvert.DeserializeObject<List<JsonUser>>(json);
            
            // Handle null result from deserialization
            if (users == null)
            {
                Log.Error(LOG_CATEGORY, "Failed to deserialize JSON - result is null");
                return new List<JsonUser>();
            }

            Log.Info(LOG_CATEGORY, $"Successfully read {users.Count} user definition(s) from file");
        }
        catch (FileNotFoundException ex)
        {
            Log.Error(LOG_CATEGORY, $"File not found: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Log.Error(LOG_CATEGORY, $"JSON parsing error: {ex.Message}");
        }
        catch (IOException ex)
        {
            Log.Error(LOG_CATEGORY, $"IO error reading file: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(LOG_CATEGORY, $"Unexpected error reading JSON file: {ex.Message}");
        }

        return users;
    }

    /// <summary>
    /// Creates a new user if they don't already exist in the system.
    /// </summary>
    /// <param name="newUser">The user definition from the JSON file</param>
    private void CreateUserIfNotExists(JsonUser newUser)
    {
        // Validate user data
        if (!ValidateUserData(newUser))
        {
            return;
        }

        // Check if user already exists
        if (UserExists(newUser.BrowseName))
        {
            usersSkipped.Add(newUser.BrowseName);
            Log.Info(LOG_CATEGORY, $"User '{newUser.BrowseName}' already exists - skipping");
            return;
        }

        // Verify the target group exists
        var usersFolder = Project.Current.Get(USERS_PATH);
        var groupsFolder = Project.Current.Get(GROUPS_PATH);

        if (usersFolder == null)
        {
            Log.Error(LOG_CATEGORY, "Security/Users folder not found in project");
            usersWithErrors.Add($"{newUser.BrowseName} (Users folder not found)");
            return;
        }

        if (groupsFolder == null)
        {
            Log.Error(LOG_CATEGORY, "Security/Groups folder not found in project");
            usersWithErrors.Add($"{newUser.BrowseName} (Groups folder not found)");
            return;
        }

        var userGroup = groupsFolder.Get<Group>(newUser.Group);
        if (userGroup == null)
        {
            Log.Error(LOG_CATEGORY, $"Group '{newUser.Group}' does not exist - cannot create user '{newUser.BrowseName}'");
            usersWithErrors.Add($"{newUser.BrowseName} (group '{newUser.Group}' not found)");
            return;
        }

        try
        {
            // Create the new user object
            var newRuntimeUser = InformationModel.MakeObject<User>(newUser.BrowseName);

            // Assign the user to the specified group
            newRuntimeUser.Refs.AddReference(FTOptix.Core.ReferenceTypes.HasGroup, userGroup);

            // Set LocaleId if specified
            if (!string.IsNullOrEmpty(newUser.LocaleId))
            {
                newRuntimeUser.LocaleId = newUser.LocaleId;
            }

            // Add user to the Users folder
            usersFolder.Add(newRuntimeUser);

            // Set the user's password
            var passwordResult = Session.ChangePassword(newRuntimeUser.BrowseName, newUser.Password, string.Empty);

            // Check password change result and handle errors
            if (!HandlePasswordResult(passwordResult, newRuntimeUser, usersFolder))
            {
                return;
            }

            usersCreated.Add(newRuntimeUser.BrowseName);
            Log.Info(LOG_CATEGORY, $"Successfully created user '{newRuntimeUser.BrowseName}' in group '{newUser.Group}'");
        }
        catch (Exception ex)
        {
            Log.Error(LOG_CATEGORY, $"Error creating user '{newUser.BrowseName}': {ex.Message}");
            usersWithErrors.Add($"{newUser.BrowseName} (creation error)");
        }
    }

    /// <summary>
    /// Validates the user data from the JSON file.
    /// </summary>
    /// <param name="user">The user data to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    private bool ValidateUserData(JsonUser user)
    {
        if (user == null)
        {
            Log.Warning(LOG_CATEGORY, "Null user entry in JSON file - skipping");
            return false;
        }

        if (string.IsNullOrWhiteSpace(user.BrowseName))
        {
            Log.Warning(LOG_CATEGORY, "User entry with empty BrowseName found - skipping");
            usersWithErrors.Add("(empty username)");
            return false;
        }

        if (string.IsNullOrWhiteSpace(user.Group))
        {
            Log.Warning(LOG_CATEGORY, $"User '{user.BrowseName}' has no group specified - skipping");
            usersWithErrors.Add($"{user.BrowseName} (no group specified)");
            return false;
        }

        if (string.IsNullOrEmpty(user.Password))
        {
            Log.Warning(LOG_CATEGORY, $"User '{user.BrowseName}' has empty password - skipping");
            usersWithErrors.Add($"{user.BrowseName} (empty password)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Handles the result of the password change operation.
    /// </summary>
    /// <param name="result">The result from Session.ChangePassword</param>
    /// <param name="user">The user object that was created</param>
    /// <param name="usersFolder">The users folder for cleanup if needed</param>
    /// <returns>True if password was set successfully, false otherwise</returns>
    private bool HandlePasswordResult(ChangePasswordResult result, User user, IUANode usersFolder)
    {
        switch (result.ResultCode)
        {
            case ChangePasswordResultCode.Success:
                return true;

            case ChangePasswordResultCode.PasswordTooShort:
                Log.Error(LOG_CATEGORY, $"Password too short for user '{user.BrowseName}' - removing user");
                usersFolder.Remove(user);
                usersWithErrors.Add($"{user.BrowseName} (password too short)");
                return false;

            case ChangePasswordResultCode.PasswordAlreadyUsed:
                Log.Warning(LOG_CATEGORY, $"Password already used for user '{user.BrowseName}'");
                // User is still created, just with a password policy warning
                return true;

            case ChangePasswordResultCode.UnsupportedOperation:
                Log.Error(LOG_CATEGORY, $"Password change not supported for user '{user.BrowseName}' - removing user");
                usersFolder.Remove(user);
                usersWithErrors.Add($"{user.BrowseName} (unsupported operation)");
                return false;

            default:
                Log.Warning(LOG_CATEGORY, $"Unexpected password result for user '{user.BrowseName}': {result.ResultCode}");
                return true;
        }
    }

    /// <summary>
    /// Checks if a user with the specified username already exists.
    /// </summary>
    /// <param name="username">The username to check</param>
    /// <returns>True if user exists, false otherwise</returns>
    private static bool UserExists(string username)
    {
        var usersFolder = Project.Current.Get(USERS_PATH);
        if (usersFolder == null)
            return false;

        // Check for case-insensitive match to prevent duplicate usernames
        return usersFolder.Children
            .OfType<User>()
            .Any(u => u.BrowseName.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds and sets the feedback message based on import results.
    /// </summary>
    private void BuildFeedbackMessage()
    {
        var feedback = new System.Text.StringBuilder();

        // Report created users
        if (usersCreated.Count > 0)
        {
            feedback.AppendLine($"✓ Users created ({usersCreated.Count}): {string.Join(", ", usersCreated)}");
        }
        else
        {
            feedback.AppendLine("✓ No new users created");
        }

        // Report skipped users (already exist)
        if (usersSkipped.Count > 0)
        {
            feedback.AppendLine($"⊘ Users skipped - already exist ({usersSkipped.Count}): {string.Join(", ", usersSkipped)}");
        }

        // Report users with errors
        if (usersWithErrors.Count > 0)
        {
            feedback.AppendLine($"✗ Users with errors ({usersWithErrors.Count}): {string.Join(", ", usersWithErrors)}");
        }

        SetFeedback(feedback.ToString().TrimEnd());
    }

    /// <summary>
    /// Sets the feedback variable value safely.
    /// </summary>
    /// <param name="message">The feedback message to display</param>
    private void SetFeedback(string message)
    {
        if (userImportFeedback != null)
        {
            userImportFeedback.Value = message;
        }
        Log.Info(LOG_CATEGORY, message.Replace("\n", " | "));
    }
    #endregion

    #region Private Classes
    /// <summary>
    /// Represents a user definition as stored in the JSON file.
    /// </summary>
    private class JsonUser
    {
        /// <summary>
        /// The username (BrowseName) for the user. This must be unique in the system.
        /// </summary>
        [JsonProperty("BrowseName")]
        public string BrowseName { get; set; }

        /// <summary>
        /// The name of the security group to assign this user to.
        /// The group must already exist in Security/Groups.
        /// </summary>
        [JsonProperty("Group")]
        public string Group { get; set; }

        /// <summary>
        /// The initial password for the user.
        /// Must comply with the project's password policy.
        /// </summary>
        [JsonProperty("Password")]
        public string Password { get; set; }

        /// <summary>
        /// Optional locale identifier for the user (e.g., "en-US", "it-IT").
        /// If not specified, the default locale will be used.
        /// </summary>
        [JsonProperty("LocaleId")]
        public string LocaleId { get; set; }

        /// <summary>
        /// Returns a string representation of this user for logging purposes.
        /// </summary>
        public override string ToString()
        {
            return $"User[BrowseName={BrowseName}, Group={Group}]";
        }
    }
    #endregion
}
