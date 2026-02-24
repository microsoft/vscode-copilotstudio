

namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using System.Text;

    /// <summary>
    /// Represents a Language Server Protocol (LSP) document with a specific semantic model type.
    /// </summary>
    /// <typeparam name="ModelType">Type for the semantic model. Usually contains a syntax tree but has broader interpretation.</typeparam>
    public abstract class LspDocument<ModelType> : LspDocument
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LspDocument{ModelType}"/> class.
        /// </summary>
        /// <param name="path">The document URI.</param>
        /// <param name="text">The document text.</param>
        /// <param name="languageId">The language ID.</param>
        /// <param name="workspacePath">The root directory path of the workspace.</param>
        public LspDocument(FilePath path, string text, string languageId, DirectoryPath workspacePath)
            : base(path, text, languageId, workspacePath)
        {
        }

        private ModelType? _fileModel = default;

        /// <summary>
        /// Gets the interpreted model of the document.
        /// </summary>
        /// <remarks>
        /// Important! This model applies to the file only. For compiled semantic, see <see cref="Workspace"/>.
        /// </remarks>
        public ModelType? FileModel
        {
            get
            {
                if (_isModelObsolete)
                {
                    _fileModel = ComputeModel();
                    _isModelObsolete = false;
                }
                return _fileModel;
            }
        }

        /// <summary>
        /// Computes the interpreted model of the document.
        /// </summary>
        /// <returns>The computed semantic model.</returns>
        protected abstract ModelType? ComputeModel();
    }

    /// <summary>
    /// Represents a Language Server Protocol (LSP) document.
    /// </summary>
    public abstract class LspDocument
    {
        private StringBuilder _textBuilder;
        protected bool _isModelObsolete;
        private string? _text = null;
        private MarkResolver? _markResolver;
        private Uri? _uri = null;
        protected readonly DirectoryPath _workspacePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="LspDocument"/> class.
        /// </summary>
        /// <param name="path">The document URI.</param>
        /// <param name="text">The document text.</param>
        /// <param name="languageId">The language ID.</param>
        /// <param name="workspacePath">The root directory path of the workspace.</param>
        public LspDocument(FilePath path, string text, string languageId, DirectoryPath workspacePath)
            : this(path, new StringBuilder(text), languageId, workspacePath)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LspDocument"/> class.
        /// </summary>
        /// <param name="path">The document URI.</param>
        /// <param name="textBuilder">The text builder for the document text.</param>
        /// <param name="languageId">The language ID.</param>
        /// <param name="workspacePath">The root directory path of the workspace.</param>
        public LspDocument(FilePath path, StringBuilder textBuilder, string? languageId, DirectoryPath workspacePath)
        {
            _workspacePath = workspacePath;
            FilePath = path;
            LanguageId = languageId;
            _textBuilder = textBuilder;
            _isModelObsolete = true;
        }

        /// <summary>
        /// Gets the mark resolver for the document.
        /// </summary>
        public MarkResolver MarkResolver
        {
            get
            {
                // either mark resolver was never initialized or text was changed
                if (_markResolver == null)
                {
                    _markResolver = new MarkResolver(Text);
                }

                return _markResolver;
            }
        }

        /// <summary>
        /// Gets or sets the document URI. Initialized lazily.
        /// Only for interfacing with client (i.e. LSP methods). Otherwise, prefer <see cref="FilePath"/> for internal usage.
        /// </summary>
        public Uri Uri => _uri ??= new Uri(FilePath.ToString());

        public FilePath FilePath { get; }

        /// <summary>
        /// Gets or sets the language ID.
        /// </summary>
        public string? LanguageId { get; set; }

        /// <summary>
        /// Gets the document text.
        /// </summary>
        public string Text
        {
            get
            {
                if (_text == null)
                {
                    _text = _textBuilder.ToString();
                }

                return _text;
            }
        }

        public ParsingResult ParsingInfo { get; } = new ParsingResult();

        /// <summary>
        /// Applies changes to the document text.
        /// </summary>
        /// <param name="changes">The changes to apply.</param>
        /// <returns>True if changes were applied, otherwise false.</returns>
        public bool ApplyChanges(TextDocumentChangeEvent[] changes)
        {
            if (changes.Length == 0)
            {
                return false;
            }

            // Most often we only get 1 change. But could be multiple. Apply in order.
            foreach (var change in changes)
            {
                if (change.Range is null)
                {
                    UpdateText(change.Text);
                }
                else
                {
                    var range = ConvertZeroBasedRangeToZeroBasedPosition(change.Range.Value);
                    if (range.Item1 == -1 || range.Item2 == -1)
                    {
                        return false;
                    }

                    if (range.Item1 > _textBuilder.Length)
                    {
                        _textBuilder.Append(change.Text);

                    }
                    else
                    {
                        _textBuilder.Remove(range.Item1, range.Item2 - range.Item1);
                        _textBuilder.Insert(range.Item1, change.Text);
                    }

                    // signal that text was changed
                    SetDirty();
                }
            }

            return true;
        }

        /// <summary>
        /// Casts the document to a specific type.
        /// </summary>
        /// <typeparam name="T">The target type.</typeparam>
        /// <returns>The document cast to the target type.</returns>
        /// <exception cref="UnsupportedLspDocumentTypeException">Thrown if the document cannot be cast to the target type.</exception>
        public T As<T>()
            where T : LspDocument
        {
            if (this is T self)
            {
                return self;
            }

            throw new UnsupportedLspDocumentTypeException(GetType().Name, typeof(T).Name);
        }

        /// <summary>
        /// Updates the document text internally.
        /// </summary>
        /// <param name="text">The new text.</param>
        private void InternalUpdateText(string text)
        {
            _textBuilder = new StringBuilder(text);
            _text = text;
            _markResolver = new MarkResolver(_text);

            // TODO: Consider updating existing Semantic Model in place by computing diff with existing document.
            // Consider updating existing Semantic Model in place by computing diff with existing document.
            // Meanwhile, the semantic model is reset and re-created.
            _isModelObsolete = true;
        }

        /// <summary>
        /// Updates the document text.
        /// </summary>
        /// <param name="text">The new text.</param>
        /// <returns>True if the text was updated, otherwise false.</returns>
        public bool UpdateText(string text)
        {
            // TODO: Optimize full text comparison - Roslyn stores doc hash in the document object.
            // optimize full text comparison - Roslyn stores doc hash in the document object.
            if (string.Equals(Text, text, StringComparison.Ordinal))
            {
                // in most cases, the document should be up-to-date in the workspace (99%)
                // through workspace change events
                return false;
            }

            InternalUpdateText(text);
            return true;
        }

        /// <summary>
        /// Marks the document as dirty, indicating that the text has changed.
        /// </summary>
        private void SetDirty()
        {
            _text = null;
            _markResolver = null;
            _isModelObsolete = true;
        }

        /// <summary>
        /// Converts a zero-based range to a zero-based position.
        /// </summary>
        /// <param name="range">The range to convert.</param>
        /// <returns>A tuple containing the start and end positions.</returns>
        private (int, int) ConvertZeroBasedRangeToZeroBasedPosition(Range range)
        {
            var start = MarkResolver.GetIndex(range.Start);
            var end = MarkResolver.GetIndex(range.End);
            return (start, end);
        }
    }
}