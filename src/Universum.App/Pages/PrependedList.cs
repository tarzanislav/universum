using System.Collections;

namespace Universum.App.Pages;

/// <summary>
/// One item before a shared list, without copying it: every row of a file has its own first item ("podľa skupiny") and
/// the same thousands of languages after it. Read only; WPF reads it by index (an <see cref="IList"/>).
/// </summary>
public sealed class PrependedList<T>(T first, IReadOnlyList<T> rest) : IReadOnlyList<T>, IList
{
    public int Count => rest.Count + 1;

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public T this[int index] => index == 0 ? first : rest[index - 1];

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<T> GetEnumerator()
    {
        yield return first;
        foreach (var item in rest)
        {
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        if (Equals(first, value))
        {
            return 0;
        }

        for (var i = 0; i < rest.Count; i++)
        {
            if (Equals(rest[i], value))
            {
                return i + 1;
            }
        }

        return -1;
    }

    public void CopyTo(Array array, int index)
    {
        foreach (var item in this)
        {
            array.SetValue(item, index++);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
