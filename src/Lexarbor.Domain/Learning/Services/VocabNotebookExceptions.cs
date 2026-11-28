namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabNotebookInvalidException() : Exception("The notebook word or meaning is missing or too long.");

public sealed class VocabNotebookItemNotFoundException() : Exception("The notebook item was not found.");

public sealed class VocabNotebookMeaningNotFoundException() : Exception("The catalog meaning was not found.");

public sealed class VocabNotebookRemovalForbiddenException() : Exception("A student cannot remove a word a teacher added.");

public sealed class VocabNotebookItemNotRejectedException() : Exception("Only a rejected notebook item can be resubmitted.");

/// <summary>A notebook write lost a race: the row changed or was removed concurrently, or its key was taken.</summary>
public sealed class VocabNotebookWriteConflictException(Exception? inner = null) : Exception("The notebook item changed concurrently.", inner);
