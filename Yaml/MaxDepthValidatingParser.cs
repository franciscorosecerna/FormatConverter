using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace FormatConverter.Yaml
{
    public class MaxDepthValidatingParser : IParser
    {
        private readonly IParser _innerParser;
        private readonly int _maxDepth;
        private readonly bool _ignoreErrors;

        private int _currentDepth = 0;
        private bool _skipMode = false;
        private int _skipDepth = 0;

        public MaxDepthValidatingParser(IParser innerParser, int maxDepth, bool ignoreErrors)
        {
            _innerParser = innerParser ?? throw new ArgumentNullException(nameof(innerParser));
            _maxDepth = maxDepth;
            _ignoreErrors = ignoreErrors;
        }

        public ParsingEvent? Current { get; private set; }

        public bool MoveNext()
        {

            while (_innerParser.MoveNext())
            {
                var current = _innerParser.Current!;
                Current = current;

                if (_skipMode)
                {
                    HandleSkipMode(current);
                    continue;
                }

                if (current is MappingStart or SequenceStart)
                {
                    _currentDepth++;

                    if (_currentDepth > _maxDepth)
                    {
                        if (_ignoreErrors)
                        {
                            Console.Error.WriteLine(
                                $"Warning: Maximum depth {_maxDepth} exceeded at line {current.Start.Line}, " +
                                $"column {current.Start.Column}. Skipping nested content."
                            );

                            _skipMode = true;
                            _skipDepth = 1;
                            continue;
                        }

                        throw new YamlException(
                            current.Start,
                            current.End,
                            $"Maximum nesting depth of {_maxDepth} exceeded at line {current.Start.Line}, " +
                            $"column {current.Start.Column}"
                        );
                    }
                }
                else if (current is MappingEnd or SequenceEnd)
                {
                    if (_currentDepth > 0)
                        _currentDepth--;
                }
                return true;
            }

            Current = null;
            return false;
        }

        private void HandleSkipMode(ParsingEvent current)
        {
            if (current is MappingStart or SequenceStart)
            {
                _skipDepth++;
            }
            else if (current is MappingEnd or SequenceEnd)
            {
                _skipDepth--;
                if (_skipDepth == 0)
                {
                    _skipMode = false;
                    if (_currentDepth > 0)
                        _currentDepth--;
                }
            }
        }
    }
}
